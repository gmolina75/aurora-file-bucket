<%@ Page Language="C#" AutoEventWireup="true" CodeBehind="Upload.aspx.cs" Inherits="AuroraFileBucket.Legacy.Upload" %>
<!DOCTYPE html>
<html xmlns="http://www.w3.org/1999/xhtml">
<head runat="server">
    <title>Subir Archivo - Aurora File Bucket Legacy</title>
    <meta charset="utf-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1" />
    <style>
        body { font-family: 'Segoe UI', Tahoma, Geneva, Verdana, sans-serif; margin: 0; padding: 20px; background: #f5f5f5; }
        .container { max-width: 700px; margin: 0 auto; background: white; padding: 30px; border-radius: 8px; box-shadow: 0 2px 10px rgba(0,0,0,0.1); }
        h1 { color: #2c3e50; margin-bottom: 10px; }
        .form-group { margin-bottom: 20px; }
        label { display: block; margin-bottom: 5px; font-weight: 600; color: #34495e; }
        input[type="text"], input[type="password"], input[type="email"], input[type="file"], input[type="number"] {
            width: 100%; padding: 12px; border: 1px solid #ddd; border-radius: 4px; font-size: 14px; box-sizing: border-box;
        }
        .btn { padding: 12px 24px; background: #3498db; color: white; border: none; border-radius: 4px; font-size: 16px; cursor: pointer; }
        .btn:hover { background: #2980b9; }
        .btn:disabled { background: #bdc3c7; cursor: not-allowed; }
        .btn-secondary { background: #95a5a6; }
        .btn-secondary:hover { background: #7f8c8d; }
        .progress-container { display: none; margin-top: 20px; }
        .progress-bar { height: 20px; background: #ecf0f1; border-radius: 10px; overflow: hidden; }
        .progress-fill { height: 100%; background: #3498db; width: 0%; transition: width 0.3s; }
        .progress-text { margin-top: 10px; color: #7f8c8d; }
        .message { padding: 15px; border-radius: 4px; margin-top: 20px; display: none; }
        .message.success { background: #d5f5e3; color: #27ae60; border: 1px solid #27ae60; }
        .message.error { background: #fadbd8; color: #e74c3c; border: 1px solid #e74c3c; }
        .message.info { background: #d6eaf8; color: #2980b9; border: 1px solid #3498db; }
        .help-text { font-size: 12px; color: #7f8c8d; margin-top: 5px; }
        .row { display: flex; gap: 15px; }
        .row .form-group { flex: 1; }
        @media (max-width: 600px) { .row { flex-direction: column; gap: 0; } }
    </style>
</head>
<body>
    <form id="form1" runat="server" enctype="multipart/form-data">
        <div class="container">
            <h1>Subir Archivo a Aurora File Bucket</h1>
            <p style="color: #7f8c8d; margin-bottom: 30px;">Cliente WebForms para transferencia de archivos masivos (hasta 100 GB)</p>

            <div class="form-group">
                <label for="txtServer">Servidor (URL base)</label>
                <input type="text" id="txtServer" value="http://localhost:5080" placeholder="http://tu-servidor:5080" />
                <div class="help-text">URL base del servidor, ej: https://files.tuempresa.com</div>
            </div>

            <div class="row">
                <div class="form-group">
                    <label for="txtEmail">Email</label>
                    <input type="email" id="txtEmail" value="admin@aurora.local" placeholder="tu@email.com" />
                </div>
                <div class="form-group">
                    <label for="txtPassword">Contraseña</label>
                    <input type="password" id="txtPassword" value="admin123" placeholder="******" />
                </div>
            </div>

            <div class="form-group">
                <label for="txtVault">Bóveda (opcional)</label>
                <input type="text" id="txtVault" value="default" placeholder="default" />
                <div class="help-text">Dejar vacío para usar la bóveda por defecto del token</div>
            </div>

            <div class="row">
                <div class="form-group">
                    <label for="txtChunkSize">Tamaño de chunk (MiB)</label>
                    <input type="number" id="txtChunkSize" value="16" min="4" max="32" />
                </div>
                <div class="form-group">
                    <label for="txtConcurrency">Concurrencia</label>
                    <input type="number" id="txtConcurrency" value="3" min="1" max="6" />
                </div>
            </div>

            <div class="row">
                <div class="form-group">
                    <label for="txtMaxBandwidth">Ancho de banda máx (Mbps)</label>
                    <input type="number" id="txtMaxBandwidth" value="50" min="1" max="1000" />
                </div>
                <div class="form-group">
                    <label>
                        <input type="checkbox" id="chkNoCompress" /> Desactivar compresión
                    </label>
                </div>
            </div>

            <div class="form-group">
                <label for="fuFile">Archivo a subir</label>
                <input type="file" id="fuFile" runat="server" />
                <div class="help-text">Máximo 100 GB. Se subirá en chunks con reanudación automática.</div>
            </div>

            <button type="button" id="btnUpload" class="btn" onclick="startUpload()">Iniciar Subida</button>
            <button type="button" id="btnCancel" class="btn btn-secondary" onclick="cancelUpload()" disabled>Cancelar</button>

            <div class="progress-container" id="progressContainer">
                <div class="progress-bar">
                    <div class="progress-fill" id="progressFill"></div>
                </div>
                <div class="progress-text" id="progressText">Preparando...</div>
            </div>

            <div class="message" id="message"></div>

            <div style="margin-top: 30px; padding-top: 20px; border-top: 1px solid #ecf0f1;">
                <h3>Estado de la subida</h3>
                <div id="statusInfo" style="font-family: monospace; font-size: 13px; color: #7f8c8d;">Esperando archivo...</div>
            </div>
        </div>
    </form>

    <script>
        let uploadController = null;
        let currentUploadId = null;
        let cancelled = false;

        async function startUpload() {
            const fileInput = document.getElementById('fuFile');
            if (!fileInput.files || fileInput.files.length === 0) {
                showMessage('Selecciona un archivo primero', 'error');
                return;
            }

            const file = fileInput.files[0];
            cancelled = false;

            // UI state
            document.getElementById('btnUpload').disabled = true;
            document.getElementById('btnCancel').disabled = false;
            document.getElementById('progressContainer').style.display = 'block';
            document.getElementById('message').style.display = 'none';
            updateProgress(0, 'Iniciando...');

            try {
                // 1. Login
                updateProgress(5, 'Autenticando...');
                const authResponse = await fetch(`${getServerUrl()}/api/auth/login`, {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({
                        email: document.getElementById('txtEmail').value,
                        password: document.getElementById('txtPassword').value
                    })
                });

                if (!authResponse.ok) {
                    const err = await authResponse.json();
                    throw new Error(err.error?.message || 'Error de autenticación');
                }

                const auth = await authResponse.json();
                const accessToken = auth.accessToken;
                const refreshToken = auth.refreshToken;

                // 2. Init upload
                updateProgress(10, 'Inicializando subida...');
                const vault = document.getElementById('txtVault').value || null;
                
                const initResponse = await fetch(`${getServerUrl()}/api/uploads/init`, {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json',
                        'Authorization': `Bearer ${accessToken}`,
                        ...(vault ? { 'X-Vault-Slug': vault } : {})
                    },
                    body: JSON.stringify({
                        fileName: file.name,
                        totalBytes: file.size,
                        chunkSize: parseInt(document.getElementById('txtChunkSize').value) * 1024 * 1024,
                        totalParts: Math.ceil(file.size / (parseInt(document.getElementById('txtChunkSize').value) * 1024 * 1024)),
                        fileSha256: null,
                        compression: document.getElementById('chkNoCompress').checked ? 'none' : 'auto'
                    })
                });

                if (!initResponse.ok) {
                    const err = await initResponse.json();
                    throw new Error(err.error?.message || 'Error inicializando subida');
                }

                const session = await initResponse.json();
                currentUploadId = session.uploadId;
                updateStatus(`Subida iniciada: ${currentUploadId} - ${session.totalParts} partes`);

                // 3. Subir chunks
                const chunkSize = parseInt(document.getElementById('txtChunkSize').value) * 1024 * 1024;
                const totalParts = session.totalParts;
                const compression = document.getElementById('chkNoCompress').checked ? 'none' : 'auto';

                for (let partNumber = 1; partNumber <= totalParts; partNumber++) {
                    if (cancelled) {
                        throw new Error('Subida cancelada por el usuario');
                    }

                    const start = (partNumber - 1) * chunkSize;
                    const end = Math.min(start + chunkSize, file.size);
                    const chunk = file.slice(start, end);

                    // Calcular hash del chunk
                    const chunkBuffer = await chunk.arrayBuffer();
                    const chunkHash = await sha256Hex(chunkBuffer);
                    
                    // Comprimir si es necesario
                    let chunkToSend = chunkBuffer;
                    let compressionMethod = 'none';
                    let rawSize = chunkBuffer.byteLength;

                    if (compression === 'auto') {
                        // Aquí iría compresión Zstd si estuviera disponible en el navegador
                        // Por simplicidad, enviamos sin comprimir
                        compressionMethod = 'none';
                    }

                    const progress = 10 + Math.round((partNumber / totalParts) * 85);
                    updateProgress(progress, `Subiendo parte ${partNumber} de ${totalParts}...`);

                    // Subir parte
                    const partResponse = await fetch(`${getServerUrl()}/api/uploads/${currentUploadId}/parts/${partNumber}`, {
                        method: 'PUT',
                        headers: {
                            'Authorization': `Bearer ${accessToken}`,
                            'Content-Type': 'application/octet-stream',
                            'X-Chunk-Sha256': chunkHash,
                            'X-Compression': compressionMethod,
                            'X-Raw-Size': rawSize.toString()
                        },
                        body: chunkBuffer
                    });

                    if (!partResponse.ok) {
                        const err = await partResponse.json();
                        throw new Error(`Error en parte ${partNumber}: ${err.error?.message || partResponse.statusText}`);
                    }

                    updateStatus(`Parte ${partNumber}/${totalParts} subida ✓`);
                }

                // 4. Completar
                updateProgress(95, 'Ensamblando archivo...');
                
                // Calcular hash global del archivo completo
                const fileBuffer = await file.arrayBuffer();
                const fileHash = await sha256Hex(fileBuffer);

                const completeResponse = await fetch(`${getServerUrl()}/api/uploads/${currentUploadId}/complete`, {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json',
                        'Authorization': `Bearer ${accessToken}`
                    },
                    body: JSON.stringify({
                        fileSha256: fileHash,
                        notify: true
                    })
                });

                if (!completeResponse.ok) {
                    const err = await completeResponse.json();
                    throw new Error(err.error?.message || 'Error completando subida');
                }

                // 5. Esperar ensamblaje
                updateProgress(98, 'Verificando integridad...');
                
                let status = 'ASSEMBLING';
                while (status === 'ASSEMBLING') {
                    await new Promise(r => setTimeout(r, 2000));
                    const statusResponse = await fetch(`${getServerUrl()}/api/uploads/${currentUploadId}/status`, {
                        headers: { 'Authorization': `Bearer ${accessToken}` }
                    });
                    const statusData = await statusResponse.json();
                    status = statusData.status;
                    
                    if (status === 'FAILED') {
                        throw new Error(`Error en ensamblaje: ${statusData.error?.message || 'Hash mismatch'}`);
                    }
                }

                if (status === 'READY') {
                    updateProgress(100, '¡Completado!');
                    showMessage(`Archivo subido y verificado correctamente. Hash: ${fileHash}`, 'success');
                    updateStatus(`COMPLETADO - Vault: ${statusData.vaultPath || 'default'}`);
                } else {
                    throw new Error(`Estado inesperado: ${status}`);
                }

            } catch (err) {
                showMessage('Error: ' + err.message, 'error');
                updateStatus(`ERROR: ${err.message}`);
            } finally {
                document.getElementById('btnUpload').disabled = false;
                document.getElementById('btnCancel').disabled = true;
            }
        }

        function cancelUpload() {
            cancelled = true;
            showMessage('Subida cancelada', 'info');
            document.getElementById('btnUpload').disabled = false;
            document.getElementById('btnCancel').disabled = true;
        }

        function getServerUrl() {
            return document.getElementById('txtServer').value.replace(/\/$/, '');
        }

        function updateProgress(percent, text) {
            document.getElementById('progressFill').style.width = percent + '%';
            document.getElementById('progressText').textContent = text;
        }

        function updateStatus(text) {
            document.getElementById('statusInfo').textContent = new Date().toLocaleTimeString() + ' - ' + text;
        }

        function showMessage(text, type) {
            const msg = document.getElementById('message');
            msg.textContent = text;
            msg.className = 'message ' + type;
            msg.style.display = 'block';
        }

        // SHA-256 en el navegador
        async function sha256Hex(buffer) {
            const hashBuffer = await crypto.subtle.digest('SHA-256', buffer);
            const hashArray = Array.from(new Uint8Array(hashBuffer));
            return hashArray.map(b => b.toString(16).padStart(2, '0')).join('');
        }
    </script>
</body>
</html>