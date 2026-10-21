// DarkCipher add-on SDK (MaskedMail and ServerManagement): the encrypted bridge (API 2).
// Compiles for .NET Framework 4.x (Windows PowerShell 5.1 loads DcxBridge.dll) and for .NET 6+ (reference this file).
//
// Handshake (the only plaintext lines):
//   app   → add-on  {"dcx":"hello","api":2,"epk":"<base64 X||Y, P-256>","salt":"<base64 32 bytes>"}
//   add-on → app    {"dcx":"hello","epk":"<base64 X||Y, P-256>"}
// Keys: ikm = SHA-256(ECDH shared secret); okm = HKDF-SHA256(ikm, salt, "dcxaddon-bridge-2|" + appEpk + "|" + addonEpk, 64)
//       app→add-on key = okm[0..32], add-on→app key = okm[32..64]. Both ephemeral: new keys for every run.
// Frames: one line each, base64(ciphertext || 16-byte tag), AES-256-GCM. The nonce is never sent: 4 bytes direction
//       (0 = app→add-on, 1 = add-on→app) + an 8-byte big-endian counter starting at 0. AAD "dcxaddon-bridge-2".
//       A frame that doesn't decrypt (changed, replayed, reordered or plain text) ends the run.
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

public static class DcxBridge
{
    private static Stream _in, _out;
    private static byte[] _recvKey, _sendKey;
    private static ulong _recvN, _sendN;
    private static readonly object WriteLock = new object();
    private static readonly byte[] Aad = Encoding.ASCII.GetBytes("dcxaddon-bridge-2");

    /// <summary>Runs the handshake on stdin/stdout and returns the app's first message (the "invoke" JSON).</summary>
    public static string Connect()
    {
        _in = Console.OpenStandardInput();
        _out = Console.OpenStandardOutput();
        var hello = ReadRawLine();
        if (hello == null) throw new IOException("the app closed the bridge");
        var appEpk = Field(hello, "epk");
        var salt = Convert.FromBase64String(Field(hello, "salt"));
        byte[] ikm;
        string myEpk;
        var p = new CngKeyCreationParameters { ExportPolicy = CngExportPolicies.None };
        using (var key = CngKey.Create(CngAlgorithm.ECDiffieHellmanP256, null, p))
        using (var peer = CngKey.Import(Blob(Convert.FromBase64String(appEpk)), CngKeyBlobFormat.EccPublicBlob))
        using (var ecdh = new ECDiffieHellmanCng(key))
        {
            var pub = key.Export(CngKeyBlobFormat.EccPublicBlob);
            var raw = new byte[64];
            Buffer.BlockCopy(pub, 8, raw, 0, 64);
            myEpk = Convert.ToBase64String(raw);
            ecdh.KeyDerivationFunction = ECDiffieHellmanKeyDerivationFunction.Hash;
            ecdh.HashAlgorithm = CngAlgorithm.Sha256;
            ikm = ecdh.DeriveKeyMaterial(peer);
        }
        var okm = Hkdf(ikm, salt, Encoding.ASCII.GetBytes("dcxaddon-bridge-2|" + appEpk + "|" + myEpk), 64);
        _recvKey = new byte[32]; _sendKey = new byte[32];
        Buffer.BlockCopy(okm, 0, _recvKey, 0, 32);
        Buffer.BlockCopy(okm, 32, _sendKey, 0, 32);
        WriteRawLine("{\"dcx\":\"hello\",\"epk\":\"" + myEpk + "\"}");
        return Read();
    }

    /// <summary>The next message from the app (JSON text), or null when the app closed the bridge.</summary>
    public static string Read()
    {
        var line = ReadRawLine();
        if (line == null) return null;
        var frame = Convert.FromBase64String(line.Trim());
        if (frame.Length < 16) throw new CryptographicException("frame too short");
        var ct = new byte[frame.Length - 16];
        var tag = new byte[16];
        Buffer.BlockCopy(frame, 0, ct, 0, ct.Length);
        Buffer.BlockCopy(frame, ct.Length, tag, 0, 16);
        var plain = Gcm.Decrypt(_recvKey, Nonce(0, _recvN++), ct, tag, Aad);
        return Encoding.UTF8.GetString(plain);
    }

    /// <summary>Sends one message (JSON text) to the app.</summary>
    public static void Write(string json)
    {
        lock (WriteLock)
        {
            byte[] tag;
            var ct = Gcm.Encrypt(_sendKey, Nonce(1, _sendN++), Encoding.UTF8.GetBytes(json), Aad, out tag);
            var frame = new byte[ct.Length + 16];
            Buffer.BlockCopy(ct, 0, frame, 0, ct.Length);
            Buffer.BlockCopy(tag, 0, frame, ct.Length, 16);
            WriteRawLine(Convert.ToBase64String(frame));
        }
    }

    private static byte[] Nonce(uint direction, ulong n)
    {
        var b = new byte[12];
        b[3] = (byte)direction;
        for (int i = 0; i < 8; i++) b[11 - i] = (byte)(n >> (8 * i));
        return b;
    }

    private static string Field(string json, string name)
    {
        var m = Regex.Match(json, "\"" + name + "\"\\s*:\\s*\"([A-Za-z0-9+/=]+)\"");
        if (!m.Success) throw new InvalidDataException("the app's hello has no " + name);
        return m.Groups[1].Value;
    }

    private static byte[] Blob(byte[] xy)
    {
        if (xy.Length != 64) throw new InvalidDataException("the app's key must be 64 bytes (P-256 X||Y)");
        var b = new byte[72];
        BitConverter.GetBytes(0x314B4345).CopyTo(b, 0);   // BCRYPT_ECDH_PUBLIC_P256_MAGIC "ECK1"
        BitConverter.GetBytes(32).CopyTo(b, 4);
        Buffer.BlockCopy(xy, 0, b, 8, 64);
        return b;
    }

    /// <summary>RFC 5869 HKDF with HMAC-SHA256.</summary>
    private static byte[] Hkdf(byte[] ikm, byte[] salt, byte[] info, int length)
    {
        byte[] prk;
        using (var h = new HMACSHA256(salt)) prk = h.ComputeHash(ikm);
        var okm = new byte[length];
        var t = new byte[0];
        int done = 0;
        for (byte i = 1; done < length; i++)
        {
            using (var h = new HMACSHA256(prk))
            {
                var input = new byte[t.Length + info.Length + 1];
                Buffer.BlockCopy(t, 0, input, 0, t.Length);
                Buffer.BlockCopy(info, 0, input, t.Length, info.Length);
                input[input.Length - 1] = i;
                t = h.ComputeHash(input);
            }
            var n = Math.Min(t.Length, length - done);
            Buffer.BlockCopy(t, 0, okm, done, n);
            done += n;
        }
        return okm;
    }

    private static string ReadRawLine()
    {
        var ms = new MemoryStream();
        while (true)
        {
            int b = _in.ReadByte();
            if (b < 0) return ms.Length == 0 ? null : Encoding.UTF8.GetString(ms.ToArray());
            if (b == '\n') return Encoding.UTF8.GetString(ms.ToArray()).TrimEnd('\r');
            if (ms.Length > 4 * 1024 * 1024) throw new InvalidDataException("line too long");
            ms.WriteByte((byte)b);
        }
    }

    private static void WriteRawLine(string s)
    {
        var b = Encoding.UTF8.GetBytes(s + "\n");
        _out.Write(b, 0, b.Length);
        _out.Flush();
    }

    /// <summary>AES-256-GCM through Windows CNG (bcrypt.dll): available to .NET Framework, where AesGcm isn't.</summary>
    private static class Gcm
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct AuthInfo
        {
            public int cbSize; public int dwInfoVersion;
            public IntPtr pbNonce; public int cbNonce;
            public IntPtr pbAuthData; public int cbAuthData;
            public IntPtr pbTag; public int cbTag;
            public IntPtr pbMacContext; public int cbMacContext;
            public int cbAAD; public long cbData; public int dwFlags;
        }

        [DllImport("bcrypt.dll", CharSet = CharSet.Unicode)] private static extern int BCryptOpenAlgorithmProvider(out IntPtr alg, string id, string impl, int flags);
        [DllImport("bcrypt.dll", CharSet = CharSet.Unicode)] private static extern int BCryptSetProperty(IntPtr h, string prop, byte[] input, int cb, int flags);
        [DllImport("bcrypt.dll")] private static extern int BCryptGenerateSymmetricKey(IntPtr alg, out IntPtr key, IntPtr obj, int cbObj, byte[] secret, int cbSecret, int flags);
        [DllImport("bcrypt.dll")] private static extern int BCryptEncrypt(IntPtr key, byte[] input, int cbInput, ref AuthInfo pad, byte[] iv, int cbIv, byte[] output, int cbOutput, out int result, int flags);
        [DllImport("bcrypt.dll")] private static extern int BCryptDecrypt(IntPtr key, byte[] input, int cbInput, ref AuthInfo pad, byte[] iv, int cbIv, byte[] output, int cbOutput, out int result, int flags);
        [DllImport("bcrypt.dll")] private static extern int BCryptDestroyKey(IntPtr key);
        [DllImport("bcrypt.dll")] private static extern int BCryptCloseAlgorithmProvider(IntPtr alg, int flags);

        private static byte[] Run(bool encrypt, byte[] key, byte[] nonce, byte[] input, byte[] aad, byte[] tag)
        {
            IntPtr alg, k;
            Check(BCryptOpenAlgorithmProvider(out alg, "AES", null, 0), "open AES");
            try
            {
                var mode = Encoding.Unicode.GetBytes("ChainingModeGCM\0");
                Check(BCryptSetProperty(alg, "ChainingMode", mode, mode.Length, 0), "GCM mode");
                Check(BCryptGenerateSymmetricKey(alg, out k, IntPtr.Zero, 0, key, key.Length, 0), "key");
                var hn = GCHandle.Alloc(nonce, GCHandleType.Pinned);
                var ha = GCHandle.Alloc(aad, GCHandleType.Pinned);
                var ht = GCHandle.Alloc(tag, GCHandleType.Pinned);
                try
                {
                    var info = new AuthInfo
                    {
                        cbSize = Marshal.SizeOf(typeof(AuthInfo)), dwInfoVersion = 1,
                        pbNonce = hn.AddrOfPinnedObject(), cbNonce = nonce.Length,
                        pbAuthData = ha.AddrOfPinnedObject(), cbAuthData = aad.Length,
                        pbTag = ht.AddrOfPinnedObject(), cbTag = tag.Length,
                    };
                    var output = new byte[input.Length];
                    int n;
                    var status = encrypt ? BCryptEncrypt(k, input, input.Length, ref info, null, 0, output, output.Length, out n, 0)
                                         : BCryptDecrypt(k, input, input.Length, ref info, null, 0, output, output.Length, out n, 0);
                    if (status == unchecked((int)0xC000A002)) throw new CryptographicException("the frame was changed, replayed or not encrypted (tag mismatch)");
                    Check(status, encrypt ? "encrypt" : "decrypt");
                    return output;
                }
                finally { hn.Free(); ha.Free(); ht.Free(); BCryptDestroyKey(k); }
            }
            finally { BCryptCloseAlgorithmProvider(alg, 0); }
        }

        public static byte[] Encrypt(byte[] key, byte[] nonce, byte[] plain, byte[] aad, out byte[] tag)
        {
            tag = new byte[16];
            return Run(true, key, nonce, plain, aad, tag);
        }

        public static byte[] Decrypt(byte[] key, byte[] nonce, byte[] ct, byte[] tag, byte[] aad) { return Run(false, key, nonce, ct, aad, tag); }

        private static void Check(int status, string what) { if (status != 0) throw new CryptographicException("CNG " + what + " failed: 0x" + status.ToString("X8")); }
    }
}
