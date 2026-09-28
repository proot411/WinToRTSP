namespace WinToRTSP.Web;

public static class WebAssets
{
    public const string IndexHtml = """
<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="UTF-8">
  <meta name="viewport" content="width=device-width, initial-scale=1.0">
  <title>WinToRTSP - Screen Casting Dashboard</title>
  <style>
    :root {
      --bg-color: #0d1117;
      --card-bg: #161b22;
      --card-border: #30363d;
      --text-main: #f0f6fc;
      --text-muted: #8b949e;
      --accent: #238636;
      --accent-hover: #2ea043;
      --danger: #da3633;
      --danger-hover: #f85149;
      --primary: #1f6feb;
      --primary-hover: #388bfd;
      --badge-bg: #21262d;
      --input-bg: #0d1117;
      --input-border: #30363d;
      --shadow: 0 8px 24px rgba(0,0,0,0.4);
      --font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, Helvetica, Arial, sans-serif;
    }

    [data-theme="light"] {
      --bg-color: #f6f8fa;
      --card-bg: #ffffff;
      --card-border: #d0d7de;
      --text-main: #1f2328;
      --text-muted: #656d76;
      --accent: #1a7f37;
      --accent-hover: #2da44e;
      --danger: #cf222e;
      --danger-hover: #a40e26;
      --primary: #0969da;
      --primary-hover: #218bff;
      --badge-bg: #eaeef2;
      --input-bg: #ffffff;
      --input-border: #d0d7de;
      --shadow: 0 8px 24px rgba(140,149,159,0.2);
    }

    * { box-sizing: border-box; margin: 0; padding: 0; }
    body {
      font-family: var(--font-family);
      background-color: var(--bg-color);
      color: var(--text-main);
      min-height: 100vh;
      display: flex;
      flex-direction: column;
      transition: background-color 0.25s, color 0.25s;
    }

    header {
      background: var(--card-bg);
      border-bottom: 1px solid var(--card-border);
      padding: 1rem 2rem;
      display: flex;
      justify-content: space-between;
      align-items: center;
      flex-wrap: wrap;
      gap: 0.75rem;
    }

    .logo-container {
      display: flex;
      align-items: center;
      gap: 0.75rem;
    }

    .logo-icon {
      width: 28px;
      height: 28px;
      background: linear-gradient(135deg, #1f6feb, #238636);
      border-radius: 6px;
      display: flex;
      align-items: center;
      justify-content: center;
      font-weight: bold;
      color: #ffffff;
      font-size: 16px;
    }

    .logo-title {
      font-size: 1.25rem;
      font-weight: 700;
      letter-spacing: -0.5px;
    }

    .header-actions {
      display: flex;
      align-items: center;
      gap: 1rem;
    }

    .theme-select {
      background: var(--input-bg);
      color: var(--text-main);
      border: 1px solid var(--input-border);
      padding: 0.4rem 0.8rem;
      border-radius: 6px;
      font-size: 0.9rem;
      cursor: pointer;
    }

    .btn {
      padding: 0.5rem 1rem;
      border-radius: 6px;
      font-size: 0.9rem;
      font-weight: 600;
      cursor: pointer;
      border: none;
      display: inline-flex;
      align-items: center;
      gap: 0.5rem;
      transition: background-color 0.15s;
    }

    .btn-primary { background: var(--primary); color: #fff; }
    .btn-primary:hover { background: var(--primary-hover); }
    .btn-accent { background: var(--accent); color: #fff; }
    .btn-accent:hover { background: var(--accent-hover); }
    .btn-danger { background: var(--danger); color: #fff; }
    .btn-danger:hover { background: var(--danger-hover); }
    .btn-secondary { background: var(--badge-bg); color: var(--text-main); border: 1px solid var(--card-border); }
    .btn-secondary:hover { opacity: 0.85; }

    main {
      max-width: 1200px;
      margin: 2rem auto;
      padding: 0 1.5rem;
      width: 100%;
      flex: 1;
    }

    .status-banner {
      background: var(--card-bg);
      border: 1px solid var(--card-border);
      border-radius: 12px;
      padding: 1.5rem;
      margin-bottom: 1.5rem;
      box-shadow: var(--shadow);
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      justify-content: space-between;
      gap: 1rem;
    }

    .stream-status-wrapper {
      display: flex;
      align-items: center;
      gap: 1rem;
    }

    .status-dot {
      width: 14px;
      height: 14px;
      border-radius: 50%;
      background: #8b949e;
    }

    .status-dot.active {
      background: #238636;
      box-shadow: 0 0 12px #2ea043;
      animation: pulse 2s infinite;
    }

    @keyframes pulse {
      0% { transform: scale(0.95); opacity: 0.8; }
      50% { transform: scale(1.15); opacity: 1; }
      100% { transform: scale(0.95); opacity: 0.8; }
    }

    .stream-url-box {
      display: flex;
      align-items: center;
      gap: 0.5rem;
      background: var(--input-bg);
      border: 1px solid var(--input-border);
      padding: 0.5rem 1rem;
      border-radius: 8px;
      font-family: monospace;
      font-size: 0.95rem;
      max-width: 100%;
      overflow-x: auto;
    }

    .stats-grid {
      display: grid;
      grid-template-columns: repeat(auto-fit, minmax(220px, 1fr));
      gap: 1.25rem;
      margin-bottom: 2rem;
    }

    .stat-card {
      background: var(--card-bg);
      border: 1px solid var(--card-border);
      border-radius: 12px;
      padding: 1.25rem;
      box-shadow: var(--shadow);
    }

    .stat-label {
      font-size: 0.85rem;
      color: var(--text-muted);
      text-transform: uppercase;
      font-weight: 600;
      letter-spacing: 0.5px;
      margin-bottom: 0.5rem;
    }

    .stat-value {
      font-size: 2rem;
      font-weight: 700;
      color: var(--text-main);
    }

    .stat-sub {
      font-size: 0.8rem;
      color: var(--text-muted);
      margin-top: 0.25rem;
    }

    .controls-grid {
      display: grid;
      grid-template-columns: repeat(auto-fit, minmax(360px, 1fr));
      gap: 1.5rem;
    }

    .control-card {
      background: var(--card-bg);
      border: 1px solid var(--card-border);
      border-radius: 12px;
      padding: 1.5rem;
      box-shadow: var(--shadow);
    }

    .card-title {
      font-size: 1.1rem;
      font-weight: 600;
      margin-bottom: 1.25rem;
      display: flex;
      align-items: center;
      gap: 0.5rem;
      border-bottom: 1px solid var(--card-border);
      padding-bottom: 0.75rem;
    }

    .form-group {
      margin-bottom: 1.25rem;
    }

    .form-group label {
      display: block;
      font-size: 0.875rem;
      font-weight: 600;
      margin-bottom: 0.5rem;
      color: var(--text-main);
    }

    .form-control {
      width: 100%;
      padding: 0.6rem 0.8rem;
      background: var(--input-bg);
      border: 1px solid var(--input-border);
      border-radius: 6px;
      color: var(--text-main);
      font-size: 0.95rem;
    }

    .form-control:focus {
      outline: none;
      border-color: var(--primary);
    }

    .range-wrapper {
      display: flex;
      align-items: center;
      gap: 1rem;
    }

    .range-input {
      flex: 1;
    }

    .range-val {
      font-family: monospace;
      font-weight: 600;
      min-width: 60px;
    }

    .checkbox-group {
      display: flex;
      align-items: center;
      gap: 0.75rem;
      cursor: pointer;
      user-select: none;
    }

    .checkbox-group input {
      width: 18px;
      height: 18px;
      cursor: pointer;
    }

    /* Modal / Auth view */
    #auth-view {
      position: fixed;
      top: 0; left: 0; right: 0; bottom: 0;
      background: rgba(0,0,0,0.8);
      display: none;
      align-items: center;
      justify-content: center;
      z-index: 1000;
    }

    .auth-box {
      background: var(--card-bg);
      border: 1px solid var(--card-border);
      border-radius: 12px;
      padding: 2.5rem;
      max-width: 420px;
      width: 90%;
      box-shadow: 0 16px 48px rgba(0,0,0,0.6);
    }

    .auth-title {
      font-size: 1.5rem;
      font-weight: 700;
      margin-bottom: 0.5rem;
      text-align: center;
    }

    .auth-sub {
      font-size: 0.875rem;
      color: var(--text-muted);
      margin-bottom: 1.5rem;
      text-align: center;
    }

    .toast {
      position: fixed;
      bottom: 2rem;
      right: 2rem;
      background: #238636;
      color: #fff;
      padding: 0.75rem 1.5rem;
      border-radius: 8px;
      box-shadow: var(--shadow);
      font-weight: 600;
      opacity: 0;
      pointer-events: none;
      transition: opacity 0.3s;
      z-index: 2000;
    }

    .toast.show { opacity: 1; }
    .toast.danger { background: var(--danger); }

    /* Keep the header navigation and panels usable on small viewports */
    @media (max-width: 720px) {
      header { padding: 0.75rem 1rem; }
      .header-actions { width: 100%; justify-content: space-between; }
      .status-banner { flex-direction: column; align-items: stretch; }
      main { margin: 1rem auto; padding: 0 0.75rem; }
    }
    @media (max-width: 480px) {
      .stats-grid, .controls-grid { grid-template-columns: 1fr; }
    }
  </style>
</head>
<body>

  <header>
    <div class="logo-container">
      <div class="logo-icon">R</div>
      <div class="logo-title">WinToRTSP</div>
    </div>
    <div class="header-actions">
      <select class="theme-select" id="themeSelector" onchange="changeTheme(this.value)">
        <option value="auto">Theme: Auto</option>
        <option value="dark">Theme: Dark</option>
        <option value="light">Theme: Light</option>
      </select>
      <button class="btn btn-secondary" onclick="logout()">Logout</button>
    </div>
  </header>

  <main>
    <!-- Stream Status Banner -->
    <div class="status-banner">
      <div class="stream-status-wrapper">
        <div class="status-dot" id="statusDot"></div>
        <div>
          <h2 id="statusText" style="font-size: 1.25rem; font-weight: 700;">Stopped</h2>
          <span style="font-size: 0.85rem; color: var(--text-muted);" id="captureMethodText">Capture: Inactive</span>
        </div>
      </div>
      <div class="stream-url-box">
        <span id="rtspUrlText">rtsp://...</span>
        <button class="btn btn-secondary" style="padding: 0.3rem 0.6rem;" onclick="copyStreamUrl()">Copy</button>
      </div>
      <div>
        <button class="btn btn-accent" id="streamToggleBtn" onclick="toggleStream()">Start Stream</button>
      </div>
    </div>

    <!-- Live Metrics Grid -->
    <div class="stats-grid">
      <div class="stat-card">
        <div class="stat-label">Active Viewers</div>
        <div class="stat-value" id="viewersVal">0</div>
        <div class="stat-sub">RTSP Clients Connected</div>
      </div>
      <div class="stat-card">
        <div class="stat-label">Live Framerate</div>
        <div class="stat-value" id="fpsVal">0 <span style="font-size: 1rem;">FPS</span></div>
        <div class="stat-sub" id="targetFpsSub">Target: 30 FPS</div>
      </div>
      <div class="stat-card">
        <div class="stat-label">Live Bitrate</div>
        <div class="stat-value" id="bitrateVal">0 <span style="font-size: 1rem;">kbps</span></div>
        <div class="stat-sub" id="targetBitrateSub">Target: 2500 kbps</div>
      </div>
      <div class="stat-card">
        <div class="stat-label">Resource Consumption</div>
        <div class="stat-value" id="cpuVal">0% <span style="font-size: 1rem; color: var(--text-muted);" id="ramVal">/ 0 MB</span></div>
        <div class="stat-sub" id="uptimeVal">Uptime: 00:00:00</div>
      </div>
    </div>

    <!-- Controls Panel -->
    <div class="controls-grid">
      <!-- Stream Configuration -->
      <div class="control-card">
        <div class="card-title">Stream & Video Settings</div>
        
        <div class="form-group">
          <label>Resolution Scaling</label>
          <select class="form-control" id="scaleSelect" onchange="saveSettings()">
            <option value="100">100% (Native Screen Resolution)</option>
            <option value="75">75% (Balanced)</option>
            <option value="50">50% (High Performance / Low Bandwidth)</option>
          </select>
        </div>

        <div class="form-group">
          <label>Target FPS</label>
          <select class="form-control" id="fpsSelect" onchange="saveSettings()">
            <option value="15">15 FPS (Ultra-low bandwidth)</option>
            <option value="24">24 FPS (Cinematic)</option>
            <option value="30">30 FPS (Standard smooth)</option>
            <option value="60">60 FPS (High-motion)</option>
          </select>
        </div>

        <div class="form-group">
          <label>Target Bitrate (kbps)</label>
          <div class="range-wrapper">
            <input type="range" class="range-input" id="bitrateRange" min="500" max="15000" step="250" oninput="updateBitrateLabel(this.value)" onchange="saveSettings()">
            <span class="range-val" id="bitrateLabel">2500</span>
          </div>
        </div>

        <div class="form-group">
          <label class="checkbox-group">
            <input type="checkbox" id="audioToggle" onchange="saveSettings()">
            <span>Enable WASAPI Desktop Audio Loopback</span>
          </label>
        </div>
      </div>

      <!-- Network & Security -->
      <div class="control-card">
        <div class="card-title">Network & Credentials</div>

        <div class="form-group">
          <label>RTSP Port</label>
          <input type="number" class="form-control" id="rtspPortInput" value="8554" onchange="saveSettings()">
        </div>

        <div class="form-group">
          <label>Stream Path</label>
          <input type="text" class="form-control" id="streamPathInput" value="/live/screen" onchange="saveSettings()">
        </div>

        <div class="form-group">
          <label>Change Password (min 12 characters)</label>
          <div style="display: flex; gap: 0.5rem;">
            <input type="password" class="form-control" id="newPasswordInput" placeholder="Enter new strong password">
            <button class="btn btn-secondary" onclick="updatePassword()">Update</button>
          </div>
        </div>
      </div>
    </div>
  </main>

  <!-- Login Modal -->
  <div id="auth-view">
    <div class="auth-box">
      <div class="auth-title">WinToRTSP Login</div>
      <div class="auth-sub">Secure session authentication with brute-force protection</div>
      
      <div class="form-group">
        <label>Username</label>
        <input type="text" class="form-control" id="loginUsername" value="admin">
      </div>
      <div class="form-group">
        <label>Password</label>
        <input type="password" class="form-control" id="loginPassword" onkeydown="if(event.key==='Enter')login()">
      </div>

      <button class="btn btn-primary" style="width: 100%; justify-content: center; margin-top: 1rem;" onclick="login()">Sign In</button>
    </div>
  </div>

  <div class="toast" id="toast">Notification</div>

  <script>
    let csrfToken = '';
    let isStreaming = false;

    // Theme Management
    function applyTheme(theme) {
      if (theme === 'auto') {
        const isDark = window.matchMedia('(prefers-color-scheme: dark)').matches;
        document.documentElement.setAttribute('data-theme', isDark ? 'dark' : 'light');
      } else {
        document.documentElement.setAttribute('data-theme', theme);
      }
    }

    function changeTheme(theme) {
      localStorage.setItem('wintortsp_theme', theme);
      applyTheme(theme);
    }

    const savedTheme = localStorage.getItem('wintortsp_theme') || 'auto';
    document.getElementById('themeSelector').value = savedTheme;
    applyTheme(savedTheme);

    window.matchMedia('(prefers-color-scheme: dark)').addEventListener('change', () => {
      if ((localStorage.getItem('wintortsp_theme') || 'auto') === 'auto') applyTheme('auto');
    });

    function showToast(msg, isDanger = false) {
      const toast = document.getElementById('toast');
      toast.textContent = msg;
      toast.className = 'toast show' + (isDanger ? ' danger' : '');
      setTimeout(() => toast.className = 'toast', 3000);
    }

    function updateBitrateLabel(val) {
      document.getElementById('bitrateLabel').textContent = val;
    }

    function copyStreamUrl() {
      const url = document.getElementById('rtspUrlText').textContent;
      navigator.clipboard.writeText(url).then(() => showToast('RTSP URL copied to clipboard!'));
    }

    // Auth & Status
    async function initDashboard() {
      try {
        const res = await fetch('/api/csrf-token');
        if (res.ok) {
          const data = await res.json();
          csrfToken = data.token;
          document.getElementById('auth-view').style.display = 'none';
          loadSettings();
          startEventStream();
        } else if (res.status === 401 || res.status === 403) {
          document.getElementById('auth-view').style.display = 'flex';
        }
      } catch (e) {
        document.getElementById('auth-view').style.display = 'flex';
      }
    }

    async function login() {
      const username = document.getElementById('loginUsername').value;
      const password = document.getElementById('loginPassword').value;

      const res = await fetch('/api/auth/login', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ username, password })
      });

      if (res.ok) {
        const data = await res.json();
        csrfToken = data.token;
        document.getElementById('auth-view').style.display = 'none';
        loadSettings();
        startEventStream();
        showToast('Login successful');
      } else {
        const err = await res.json().catch(() => ({}));
        showToast(err.message || 'Invalid credentials or IP blocked', true);
      }
    }

    async function logout() {
      await fetch('/api/auth/logout', {
        method: 'POST',
        headers: { 'X-CSRF-Token': csrfToken }
      });
      location.reload();
    }

    async function loadSettings() {
      const res = await fetch('/api/status');
      if (!res.ok) return;
      const status = await res.json();
      updateUI(status);
    }

    function updateUI(s) {
      isStreaming = s.isStreaming;
      const dot = document.getElementById('statusDot');
      const text = document.getElementById('statusText');
      const btn = document.getElementById('streamToggleBtn');

      if (s.isStreaming) {
        dot.className = 'status-dot active';
        text.textContent = 'Streaming Live';
        btn.textContent = 'Stop Stream';
        btn.className = 'btn btn-danger';
      } else {
        dot.className = 'status-dot';
        text.textContent = 'Stopped';
        btn.textContent = 'Start Stream';
        btn.className = 'btn btn-accent';
      }

      document.getElementById('captureMethodText').textContent =
        `Capture: ${s.captureMethod} | Encoder: ${s.encoderName}`;

      document.getElementById('rtspUrlText').textContent = s.streamUrl;
      document.getElementById('viewersVal').textContent = s.activeViewers;
      document.getElementById('fpsVal').innerHTML = `${s.currentFps} <span style="font-size: 1rem;">FPS</span>`;
      document.getElementById('targetFpsSub').textContent = `Target: ${s.targetFps} FPS (${s.resolutionWidth}x${s.resolutionHeight})`;
      document.getElementById('bitrateVal').innerHTML = `${s.currentBitrateKbps} <span style="font-size: 1rem;">kbps</span>`;
      document.getElementById('targetBitrateSub').textContent = `Target: ${s.targetBitrateKbps} kbps`;
      document.getElementById('cpuVal').innerHTML = `${s.cpuPercent}% <span style="font-size: 1rem; color: var(--text-muted);">/ ${s.ramMb} MB</span>`;
      
      const totalSec = Math.floor(s.uptimeTotalSeconds || 0);
      const h = String(Math.floor(totalSec / 3600)).padStart(2, '0');
      const m = String(Math.floor((totalSec % 3600) / 60)).padStart(2, '0');
      const sec = String(totalSec % 60).padStart(2, '0');
      document.getElementById('uptimeVal').textContent = `Uptime: ${h}:${m}:${sec}`;
    }

    async function toggleStream() {
      const endpoint = isStreaming ? '/api/stream/stop' : '/api/stream/start';
      const res = await fetch(endpoint, {
        method: 'POST',
        headers: { 'X-CSRF-Token': csrfToken }
      });
      if (res.ok) {
        showToast(isStreaming ? 'Stream stopped' : 'Stream started');
        loadSettings();
      } else {
        const err = await res.json().catch(() => ({}));
        // Results.Problem() sends RFC7807 ("detail"); other endpoints send "message".
        showToast(err.detail || err.message || 'Operation failed', true);
      }
    }

    async function saveSettings() {
      const payload = {
        resolutionScalePercent: parseInt(document.getElementById('scaleSelect').value, 10),
        targetFps: parseInt(document.getElementById('fpsSelect').value, 10),
        bitrateKbps: parseInt(document.getElementById('bitrateRange').value, 10),
        enableAudio: document.getElementById('audioToggle').checked,
        rtspPort: parseInt(document.getElementById('rtspPortInput').value, 10),
        streamPath: document.getElementById('streamPathInput').value
      };

      const res = await fetch('/api/settings', {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          'X-CSRF-Token': csrfToken
        },
        body: JSON.stringify(payload)
      });

      if (res.ok) {
        showToast('Settings saved successfully');
      } else {
        showToast('Failed to save settings', true);
      }
    }

    async function updatePassword() {
      const newPassword = document.getElementById('newPasswordInput').value;
      if (!newPassword || newPassword.length < 12) {
        showToast('Password must be at least 12 characters', true);
        return;
      }

      const res = await fetch('/api/settings/password', {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          'X-CSRF-Token': csrfToken
        },
        body: JSON.stringify({ newPassword })
      });

      if (res.ok) {
        showToast('Password updated successfully');
        document.getElementById('newPasswordInput').value = '';
      } else {
        const err = await res.json().catch(() => ({}));
        showToast(err.message || 'Failed to update password', true);
      }
    }

    function startEventStream() {
      const evtSource = new EventSource('/api/events');
      evtSource.onmessage = (event) => {
        try {
          const status = JSON.parse(event.data);
          updateUI(status);
        } catch (e) {}
      };
      evtSource.onerror = () => {
        evtSource.close();
        setTimeout(startEventStream, 3000);
      };
    }

    initDashboard();
  </script>
</body>
</html>
""";
}
