"""Command line entry point of the Aurora File Bucket sender (Linux client).

Usage::

    aurora-send --server https://host --file /data/big.iso \\
                --email user@example.com --password secret
"""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

from .api import ApiClient, ApiError, TokenStore, normalize_base_url
from .config import (
    DEFAULT_CHUNK_SIZE_MIB,
    DEFAULT_CONCURRENCY,
    DEFAULT_MAX_BANDWIDTH_MBPS,
    DEFAULT_MAX_RETRIES,
    DEFAULT_STATE_DB,
    MAX_CHUNK_SIZE_MIB,
    MAX_CONCURRENCY,
    MAX_MAX_BANDWIDTH_MBPS,
    MIN_CHUNK_SIZE_MIB,
    MIN_CONCURRENCY,
    MIN_MAX_BANDWIDTH_MBPS,
    SenderConfig,
    build_config,
)
from .progress import ProgressReporter
from .state import StateStore
from .uploader import Uploader

__all__ = ["build_parser", "main"]


def _bounded_int(minimum: int, maximum: int):
    """argparse type for an integer inside ``[minimum, maximum]``."""

    def parse(value: str) -> int:
        try:
            number = int(value)
        except ValueError:
            raise argparse.ArgumentTypeError(f"{value!r} is not an integer") from None
        if not minimum <= number <= maximum:
            raise argparse.ArgumentTypeError(
                f"must be between {minimum} and {maximum} (got {number})"
            )
        return number

    return parse


def build_parser() -> argparse.ArgumentParser:
    """Build the argument parser of the sender."""
    parser = argparse.ArgumentParser(
        prog="aurora-send",
        description=(
            "Upload a single file (up to 100 GB) to an Aurora File Bucket receiver: "
            "resumable, integrity verified, bandwidth limited."
        ),
        epilog=(
            "Examples:\n"
            "  aurora-send --server https://files.example.com --file /data/big.iso \\\n"
            "             --email user@example.com --password secret\n"
            "  aurora-send --server https://files.example.com --file /data/big.iso \\\n"
            "             --token eyJhbGciOi...\n"
            "  aurora-send status --server https://files.example.com --upload-id 3f9a1c6e-..."
        ),
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )

    parser.add_argument(
        "command",
        nargs="?",
        choices=("upload", "status"),
        default="upload",
        help="operation to run: upload (default) or status",
    )
    parser.add_argument(
        "--server",
        metavar="URL",
        default=None,
        help="base URL of the receiver, e.g. https://files.example.com (required)",
    )
    parser.add_argument(
        "--file",
        metavar="PATH",
        type=Path,
        default=None,
        help="path of the file to upload (required for upload)",
    )
    parser.add_argument(
        "--vault",
        metavar="SLUG",
        default=None,
        help="target vault slug (default: the first vault allowed by the token)",
    )
    parser.add_argument(
        "--email",
        metavar="ADDRESS",
        default=None,
        help="account e-mail used to obtain an access token",
    )
    parser.add_argument(
        "--password",
        metavar="SECRET",
        default=None,
        help="password used to obtain an access token",
    )
    parser.add_argument(
        "--token",
        metavar="JWT",
        default=None,
        help="reuse an existing access token instead of logging in",
    )
    parser.add_argument(
        "--chunk-size-mib",
        metavar="MIB",
        type=_bounded_int(MIN_CHUNK_SIZE_MIB, MAX_CHUNK_SIZE_MIB),
        default=DEFAULT_CHUNK_SIZE_MIB,
        help=f"chunk size in MiB, {MIN_CHUNK_SIZE_MIB}-{MAX_CHUNK_SIZE_MIB} (default: {DEFAULT_CHUNK_SIZE_MIB})",
    )
    parser.add_argument(
        "--concurrency",
        metavar="N",
        type=_bounded_int(MIN_CONCURRENCY, MAX_CONCURRENCY),
        default=DEFAULT_CONCURRENCY,
        help=f"parts transferred in parallel, {MIN_CONCURRENCY}-{MAX_CONCURRENCY} (default: {DEFAULT_CONCURRENCY})",
    )
    parser.add_argument(
        "--max-bandwidth-mbps",
        metavar="MBPS",
        type=_bounded_int(MIN_MAX_BANDWIDTH_MBPS, MAX_MAX_BANDWIDTH_MBPS),
        default=DEFAULT_MAX_BANDWIDTH_MBPS,
        help=(
            "global bandwidth limit in Mbps shared by every worker "
            f"(default: {DEFAULT_MAX_BANDWIDTH_MBPS})"
        ),
    )
    parser.add_argument(
        "--no-compress",
        action="store_true",
        help="disable zstd compression for the whole session",
    )
    parser.add_argument(
        "--state-db",
        metavar="PATH",
        type=Path,
        default=Path(DEFAULT_STATE_DB),
        help=f"SQLite database holding the resume state (default: {DEFAULT_STATE_DB})",
    )
    parser.add_argument(
        "--max-retries",
        metavar="N",
        type=_bounded_int(0, 20),
        default=DEFAULT_MAX_RETRIES,
        help=f"maximum retries per request (default: {DEFAULT_MAX_RETRIES})",
    )
    parser.add_argument(
        "--no-resume",
        action="store_true",
        help="ignore the local resume state and start a new session",
    )
    parser.add_argument(
        "--json-progress",
        action="store_true",
        help="emit machine readable progress lines (JSON) on stdout",
    )
    parser.add_argument(
        "--upload-id",
        metavar="UUID",
        default=None,
        help="upload session to inspect (only used by the status command)",
    )
    return parser


def _obtain_tokens(config: SenderConfig, api: ApiClient, tokens: TokenStore) -> None:
    """Log in when no access token was supplied."""
    if config.access_token:
        return
    tokens.update(api.login(str(config.email), str(config.password)))


def _run_upload(config: SenderConfig) -> int:
    """Run the upload command."""
    if not config.file_path.is_file():
        print(f"error: {config.file_path} is not a file", file=sys.stderr)
        return 1

    state = StateStore(config.state_db)
    api = None
    try:
        tokens = TokenStore(config.access_token or "")
        api = ApiClient(
            config.server_url,
            tokens,
            client_id=config.client_id,
            max_retries=config.max_retries,
        )
        _obtain_tokens(config, api, tokens)

        total_bytes = config.file_path.stat().st_size
        total_parts = -(-total_bytes // config.chunk_size)
        reporter = ProgressReporter(
            total_parts,
            total_bytes,
            json_mode=config.json_progress,
        )
        uploader = Uploader(config, api, state, reporter)
        result = uploader.run()
    except ApiError as error:
        print(f"error: {error}", file=sys.stderr)
        return 1
    except Exception as error:  # noqa: BLE001 - the CLI reports any failure
        print(f"error: {error}", file=sys.stderr)
        return 1
    finally:
        if api is not None:
            api.close()
        state.close()

    reporter.summary(
        {
            "uploadId": result.upload_id,
            "fileSha256": result.file_sha256,
            "partsUploaded": result.parts_uploaded,
            "partsSkipped": result.parts_skipped,
            "totalParts": result.total_parts,
            "bytesTransferred": result.bytes_transferred,
            "durationSeconds": round(result.duration_seconds, 1),
            "vaultPath": result.vault_path,
            "finalSha256": result.final_sha256,
        }
    )
    if not config.json_progress:
        print(f"upload {result.upload_id}: {result.status} in {result.duration_seconds:.1f}s")
        print(f"  file sha256 : {result.file_sha256}")
        print(f"  vault path  : {result.vault_path}")
        if result.parts_skipped:
            print(f"  resumed     : {result.parts_skipped} parts already on the server")
    return 0


def main(argv: list[str] | None = None) -> int:
    """Entry point of the ``aurora-send`` console script."""
    parser = build_parser()
    args = parser.parse_args(argv)

    if args.command == "status":
        return _status_command(args, parser)

    try:
        config = build_config(args)
    except ValueError as error:
        parser.error(str(error))
        return 2  # pragma: no cover - parser.error exits

    return _run_upload(config)


def _status_command(args: argparse.Namespace, parser: argparse.ArgumentParser) -> int:
    """Query the server for the state of an existing upload."""
    if not args.server:
        parser.error("--server is required for the status command")
    if not args.upload_id:
        parser.error("--upload-id is required for the status command")
    if not args.token and not (args.email and args.password):
        parser.error("either --token or both --email and --password are required")

    tokens = TokenStore(args.token or "")
    api = ApiClient(normalize_base_url(args.server), tokens, client_id="aurora-send-status")
    try:
        if not args.token:
            tokens.update(api.login(str(args.email), str(args.password)))
        status = api.get_status(str(args.upload_id))
    except ApiError as error:
        print(f"error: {error}", file=sys.stderr)
        return 1
    except Exception as error:  # noqa: BLE001 - the CLI reports any failure
        print(f"error: {error}", file=sys.stderr)
        return 1
    finally:
        api.close()

    print(json.dumps(status, indent=2, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
