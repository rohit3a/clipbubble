using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace ClipBubble;

sealed class PairingException(string message) : Exception(message);

/// authorized_keys holds keys that aren't ClipBubble's. Every line it doesn't own stays byte for byte, because decoding
/// the file as text would rewrite any line that isn't valid UTF-8.
static class AuthorizedKeys
{
    public static void Add(string file, string line, string keyBody)
    {
        var lines = Lines(file).Where(l => !Has(l, keyBody)).ToList();
        while (lines.Count > 0 && lines[lines.Count - 1].Length == 0) lines.RemoveAt(lines.Count - 1);
        lines.Add(Encoding.UTF8.GetBytes(line));
        Write(file, lines);
    }

    static List<byte[]> Lines(string file)
    {
        var lines = new List<byte[]>();
        if (!File.Exists(file)) return lines;
        var data = File.ReadAllBytes(file);
        var start = 0;
        for (var i = 0; i <= data.Length; i++)
        {
            if (i < data.Length && data[i] != 10) continue;
            lines.Add(data.Skip(start).Take(i - start).ToArray());
            start = i + 1;
        }
        return lines;
    }

    static bool Has(byte[] line, string text) => Encoding.ASCII.GetString(line).Contains(text);

    /// To a temp file first, so a full disk fails before the keys are touched. Replace keeps the file's ACL, which sshd checks.
    static void Write(string file, List<byte[]> lines)
    {
        var tmp = file + ".clipbubble";
        File.WriteAllBytes(tmp, lines.SelectMany(l => l.Append((byte)10)).ToArray());
        if (File.Exists(file)) File.Replace(tmp, file, null);
        else File.Move(tmp, file);
    }
}

/// Links an iPhone without a terminal, as the Mac (Sources/Pairing.swift) and Linux do. The QR code carries this PC's
/// addresses and a one-time code. The phone sends its SSH key back over the local network, and the key goes where
/// OpenSSH Server reads it, allowed to run only ClipBubble's relay.
sealed class Pairing
{
    /// A fixed port, so one firewall rule covers pairing. Any free port does if another app has it.
    public const int FixedPort = 47821;
    const string FirewallRule = "ClipBubble iPhone pairing";

    readonly string code;
    readonly string name;
    readonly TcpListener listener;
    readonly Action<string> done;
    readonly Timer expiry;
    int stopped;

    /// `done` gets what to show once it's over (linked, expired or failed), on a background thread.
    public Pairing(string name, Action<string> done)
    {
        var bytes = new byte[16];
        using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
        code = string.Concat(bytes.Select(b => b.ToString("x2")));
        this.name = name;
        this.done = done;
        try { listener = new TcpListener(IPAddress.Any, FixedPort); listener.Start(); }
        catch (SocketException) { listener = new TcpListener(IPAddress.Any, 0); listener.Start(); }
        new Thread(Serve) { IsBackground = true, Name = "ClipBubble pairing" }.Start();
        expiry = new Timer(_ => Finish("That code expired. Click Link iPhone… for a new one."), null, TimeSpan.FromMinutes(10), Timeout.InfiniteTimeSpan);
    }

    public string Link
    {
        get
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            return $"clipbubble://pair?u={Uri.EscapeDataString(Environment.UserName)}&h={string.Join(",", Addresses())}" +
                   $"&p={port}&c={code}&n={Uri.EscapeDataString(name)}";
        }
    }

    /// Stops listening. True for the call that actually stopped it.
    public bool Stop()
    {
        if (Interlocked.Exchange(ref stopped, 1) == 1) return false;
        expiry.Dispose();
        listener.Stop();
        return true;
    }

    void Finish(string note)
    {
        if (Stop()) done(note);
    }

    void Serve()
    {
        while (true)
        {
            TcpClient c;
            try { c = listener.AcceptTcpClient(); }
            catch (Exception e) when (e is SocketException or ObjectDisposedException or InvalidOperationException) { return; }
            using (c)
            {
                var phone = Answer(c);
                if (phone != null) { Finish($"Linked {phone}. It syncs whenever ClipBubble is open there."); return; }
            }
        }
    }

    /// One request line of JSON in, one line out. The phone's name when it worked.
    string? Answer(TcpClient c)
    {
        var stream = c.GetStream();
        c.ReceiveTimeout = 10000;
        var buf = new List<byte>();
        try
        {
            var chunk = new byte[4096];
            while (!buf.Contains((byte)'\n') && buf.Count <= 8192)
            {
                var n = stream.Read(chunk, 0, chunk.Length);
                if (n == 0) return null;
                buf.AddRange(chunk.Take(n));
            }
        }
        catch (IOException) { return null; }
        var line = Encoding.UTF8.GetString(buf.TakeWhile(b => b != '\n').ToArray());
        var reply = new Dictionary<string, object> { ["ok"] = false };
        string? phone = null;
        try
        {
            var req = Store.Json.Deserialize<Dictionary<string, object>>(line);
            if (req != null && req.TryGetValue("code", out var got) && got is string given && SameCode(given)
                && req.TryGetValue("key", out var k) && k is string key && req.TryGetValue("name", out var nm) && nm is string phoneName)
            {
                Authorize(key, phoneName);
                reply = new Dictionary<string, object> { ["ok"] = true, ["user"] = Environment.UserName, ["name"] = name, ["hostKey"] = HostKey() ?? "" };
                phone = phoneName;
            }
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException) { }
        catch (PairingException e) { reply["error"] = e.Message; }
        try
        {
            var out_ = Encoding.UTF8.GetBytes(Store.Json.Serialize(reply) + "\n");
            stream.Write(out_, 0, out_.Length);
        }
        catch (IOException) { }
        return phone;
    }

    bool SameCode(string given)
    {
        if (given.Length != code.Length) return false;
        var diff = 0;
        for (var i = 0; i < code.Length; i++) diff |= given[i] ^ code[i];
        return diff == 0;
    }

    /// Adds the phone's key, limited to running this app's relay: no shell, no forwarding, no terminal. An
    /// administrator's keys live in one file shared by all administrators, which only an elevated process can write,
    /// so Windows asks first.
    public static void Authorize(string key, string phone)
    {
        var parts = key.Split(' ');
        if (parts.Length != 2 || parts[0] != "ssh-ed25519" || parts[1].Length == 0 || !parts[1].All(ch => ch < 128 && (char.IsLetterOrDigit(ch) || "+/=".IndexOf(ch) >= 0)))
            throw new PairingException("That isn't an iPhone key ClipBubble made.");
        var exe = Process.GetCurrentProcess().MainModule!.FileName;
        var label = new string(phone.Where(ch => ch < 128 && (char.IsLetterOrDigit(ch) || ch is ' ' or '-')).Take(40).ToArray());
        var line = $"restrict,command=\"\\\"{exe}\\\" --peer\" {key} ClipBubble on {label}";
        if (UsesAdministratorsKeys()) WriteElevated(line, parts[1]);
        else WriteUserKeys(line, parts[1]);
    }

    static void WriteUserKeys(string line, string keyBody)
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
        var file = Path.Combine(dir, "authorized_keys");
        try
        {
            Directory.CreateDirectory(dir);
            AuthorizedKeys.Add(file, line, keyBody);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new PairingException("Couldn't add the iPhone's key to " + file + ".");
        }
    }

    static void WriteElevated(string line, string keyBody)
    {
        static string Quote(string s) => "'" + s.Replace("'", "''") + "'";
        var script = $@"$ErrorActionPreference = 'Stop'
$f = Join-Path $env:ProgramData 'ssh\administrators_authorized_keys'
$lines = @(if (Test-Path $f) {{ Get-Content $f | Where-Object {{ $_ -and -not $_.Contains({Quote(keyBody)}) }} }})
$lines += {Quote(line)}
[IO.File]::WriteAllText($f, (($lines -join ""`n"") + ""`n""))
icacls $f /inheritance:r /grant '*S-1-5-32-544:F' /grant '*S-1-5-18:F' | Out-Null
if ($LASTEXITCODE -ne 0) {{ exit 1 }}";
        if (RunElevated(script) != 0) throw new PairingException("Windows couldn't add the iPhone's key for administrators.");
    }

    /// Runs PowerShell as administrator, after Windows asks the user. Its exit code.
    static int RunElevated(string script)
    {
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var info = new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encoded)
        {
            UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden,
        };
        try
        {
            using var p = Process.Start(info)!;
            p.WaitForExit();
            return p.ExitCode;
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223)
        {
            throw new PairingException("Windows needs your OK for that. Click Link iPhone… again and choose Yes.");
        }
    }

    /// OpenSSH for Windows reads an administrator's keys from one shared file, unless sshd_config was changed.
    static bool UsesAdministratorsKeys()
    {
        if (!IsAdministrator()) return false;
        var config = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ssh", "sshd_config");
        try
        {
            return File.ReadAllLines(config).Any(l => l.TrimStart().StartsWith("Match Group administrators", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return true; }
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool GetTokenInformation(IntPtr token, int cls, out int info, int size, out int needed);

    static bool IsAdministrator()
    {
        using var id = WindowsIdentity.GetCurrent();
        if (new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator)) return true;
        // Under UAC an administrator runs with a filtered token that hides the group. Its elevation type gives it away.
        const int TokenElevationType = 18, Limited = 3;
        return GetTokenInformation(id.Token, TokenElevationType, out var type, sizeof(int), out _) && type == Limited;
    }

    /// The key sshd shows clients, so the phone checks it's talking to this PC from the first connection.
    static string? HostKey()
    {
        var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ssh", "ssh_host_ed25519_key.pub");
        try { return string.Join(" ", File.ReadAllText(file).Split(new[] { ' ', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).Take(2)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// The IPv4 address of every adapter a phone could reach: Wi-Fi and Ethernet first, never VMs, WSL or containers.
    static IEnumerable<string> Addresses()
    {
        var skip = new[] { "virtual", "hyper-v", "vethernet", "vmware", "virtualbox", "wsl", "docker", "bluetooth", "loopback" };
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .Where(n => !skip.Any(w => (n.Name + " " + n.Description).IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0))
            .OrderBy(n => n.NetworkInterfaceType is NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.Ethernet ? 0 : 1)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !a.Address.ToString().StartsWith("169.254."))
            .Select(a => a.Address.ToString())
            .Distinct();
    }

    /// OpenSSH Server answers, and the firewall lets the phone reach pairing.
    public static bool Ready() => SshServerOn() && FirewallAllowsPairing();

    static bool SshServerOn()
    {
        try
        {
            using var c = new TcpClient();
            return c.ConnectAsync(IPAddress.Loopback, 22).Wait(500) && c.Connected;
        }
        catch (Exception e) when (e is SocketException or AggregateException) { return false; }
    }

    static bool FirewallAllowsPairing()
    {
        var info = new ProcessStartInfo("netsh", $"advfirewall firewall show rule name=\"{FirewallRule}\"")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
        };
        try
        {
            using var p = Process.Start(info)!;
            p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            return p.ExitCode == 0;
        }
        catch (Win32Exception) { return false; }
    }

    /// Turns on OpenSSH Server, which the phone's SSH connection reaches, and lets the phone through the firewall
    /// for it and for pairing. Windows asks first. Installing the server can take a few minutes.
    public static void SetUp()
    {
        var script = $@"$ErrorActionPreference = 'Stop'
$c = Get-WindowsCapability -Online | Where-Object Name -like 'OpenSSH.Server*' | Select-Object -First 1
if ($c.State -ne 'Installed') {{ Add-WindowsCapability -Online -Name $c.Name | Out-Null }}
Set-Service -Name sshd -StartupType Automatic
Start-Service sshd
if (-not (Get-NetFirewallRule -Name 'OpenSSH-Server-In-TCP' -ErrorAction SilentlyContinue)) {{
  New-NetFirewallRule -Name 'OpenSSH-Server-In-TCP' -DisplayName 'OpenSSH Server (sshd)' -Direction Inbound -Protocol TCP -LocalPort 22 -Action Allow | Out-Null
}}
if (-not (Get-NetFirewallRule -DisplayName '{FirewallRule}' -ErrorAction SilentlyContinue)) {{
  New-NetFirewallRule -DisplayName '{FirewallRule}' -Direction Inbound -Protocol TCP -LocalPort {FixedPort} -Action Allow | Out-Null
}}";
        if (RunElevated(script) != 0 || !SshServerOn())
            throw new PairingException("Couldn't turn on OpenSSH Server. Turn it on in Settings → System → Optional features, then try again.");
    }
}
