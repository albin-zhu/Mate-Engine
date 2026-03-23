using System;
using System.Diagnostics;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace MateEngine.Agent
{
    /// <summary>
    /// Loads OpenClaw device identity (Ed25519 keys) from ~/.openclaw/identity/device.json
    /// and provides signing via Node.js crypto (Ed25519 is not available in .NET Standard 2.1).
    /// </summary>
    public class DeviceIdentity
    {
        public string DeviceId { get; private set; }
        public string PublicKeyBase64Url { get; private set; }

        string _privateKeyPem;
        string _publicKeyPem;

        DeviceIdentity() { }

        /// <summary>Load device identity from ~/.openclaw/identity/device.json</summary>
        public static DeviceIdentity Load()
        {
            string path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".openclaw", "identity", "device.json");

            if (!File.Exists(path))
            {
                Debug.LogWarning($"[DeviceIdentity] No device.json at {path}");
                return null;
            }

            try
            {
                var json = JObject.Parse(File.ReadAllText(path));
                string deviceId = json["deviceId"]?.ToString();
                string privPem = json["privateKeyPem"]?.ToString();
                string pubPem = json["publicKeyPem"]?.ToString();

                if (string.IsNullOrEmpty(deviceId) || string.IsNullOrEmpty(privPem) || string.IsNullOrEmpty(pubPem))
                {
                    Debug.LogWarning("[DeviceIdentity] device.json missing fields");
                    return null;
                }

                // Extract raw 32-byte public key from SPKI PEM for base64url
                byte[] pubRaw = ExtractEd25519PublicKey(pubPem);
                if (pubRaw == null)
                {
                    Debug.LogWarning("[DeviceIdentity] Failed to parse public key from PEM");
                    return null;
                }

                var identity = new DeviceIdentity
                {
                    DeviceId = deviceId,
                    _privateKeyPem = privPem,
                    _publicKeyPem = pubPem,
                    PublicKeyBase64Url = ToBase64Url(pubRaw)
                };

                Debug.Log($"[DeviceIdentity] Loaded device {deviceId.Substring(0, 12)}...");
                return identity;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[DeviceIdentity] Load error: {e.Message}");
                return null;
            }
        }

        /// <summary>Sign a UTF-8 payload using Ed25519 via Node.js and return base64url signature.</summary>
        public string Sign(string payload)
        {
            try
            {
                // Use Node.js crypto to sign (Ed25519 not available in .NET Standard 2.1)
                string escapedPayload = payload.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");
                string escapedKey = _privateKeyPem.Replace("\n", "\\n");

                string script = $@"
const crypto = require('crypto');
const key = crypto.createPrivateKey('{escapedKey}');
const sig = crypto.sign(null, Buffer.from('{escapedPayload}'), key);
process.stdout.write(sig.toString('base64url'));
";

                var psi = new ProcessStartInfo
                {
                    FileName = "node",
                    Arguments = $"-e \"{script.Replace("\"", "\\\"")}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                // Try inline eval first
                string result = RunNodeSign(payload);
                if (result != null) return result;

                Debug.LogWarning("[DeviceIdentity] Node.js signing failed");
                return "";
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[DeviceIdentity] Sign error: {e.Message}");
                return "";
            }
        }

        string RunNodeSign(string payload)
        {
            try
            {
                // Write a temp script file to avoid shell escaping issues
                string tempScript = Path.Combine(Path.GetTempPath(), "openclaw_sign.js");
                string tempPayload = Path.Combine(Path.GetTempPath(), "openclaw_payload.txt");
                string tempKey = Path.Combine(Path.GetTempPath(), "openclaw_privkey.pem");

                File.WriteAllText(tempPayload, payload);
                File.WriteAllText(tempKey, _privateKeyPem);
                File.WriteAllText(tempScript,
@"const crypto = require('crypto');
const fs = require('fs');
const payload = fs.readFileSync(process.argv[2], 'utf8');
const key = crypto.createPrivateKey(fs.readFileSync(process.argv[3], 'utf8'));
const sig = crypto.sign(null, Buffer.from(payload), key);
process.stdout.write(sig.toString('base64url'));
");

                var psi = new ProcessStartInfo
                {
                    FileName = "node",
                    Arguments = $"\"{tempScript}\" \"{tempPayload}\" \"{tempKey}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using (var proc = Process.Start(psi))
                {
                    string output = proc.StandardOutput.ReadToEnd();
                    string error = proc.StandardError.ReadToEnd();
                    proc.WaitForExit(5000);

                    // Clean up temp files
                    try { File.Delete(tempScript); } catch { }
                    try { File.Delete(tempPayload); } catch { }
                    try { File.Delete(tempKey); } catch { }

                    if (proc.ExitCode == 0 && !string.IsNullOrEmpty(output))
                        return output.Trim();

                    if (!string.IsNullOrEmpty(error))
                        Debug.LogWarning($"[DeviceIdentity] Node error: {error}");
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[DeviceIdentity] RunNodeSign error: {e.Message}");
            }
            return null;
        }

        /// <summary>Extract 32-byte Ed25519 public key from SPKI PEM.</summary>
        static byte[] ExtractEd25519PublicKey(string pem)
        {
            string b64 = pem
                .Replace("-----BEGIN PUBLIC KEY-----", "")
                .Replace("-----END PUBLIC KEY-----", "")
                .Replace("\n", "").Replace("\r", "").Trim();

            byte[] der = Convert.FromBase64String(b64);

            // SPKI Ed25519: total 44 bytes, key at offset 12
            if (der.Length == 44 && der[0] == 0x30)
            {
                byte[] key = new byte[32];
                Array.Copy(der, 12, key, 0, 32);
                return key;
            }

            // Fallback: search for 03 21 00 pattern (BIT STRING)
            for (int i = 0; i < der.Length - 34; i++)
            {
                if (der[i] == 0x03 && der[i + 1] == 0x21 && der[i + 2] == 0x00)
                {
                    byte[] key = new byte[32];
                    Array.Copy(der, i + 3, key, 0, 32);
                    return key;
                }
            }

            return null;
        }

        static string ToBase64Url(byte[] data)
        {
            return Convert.ToBase64String(data)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }
    }
}
