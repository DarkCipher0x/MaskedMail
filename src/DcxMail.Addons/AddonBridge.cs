using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace DcxMail.Addons;

/// <summary>
/// The app's end of the encrypted add-on bridge (API 2, ADR-035). Per run: an ephemeral P-256 ECDH key pair and a random
/// salt; ikm = SHA-256(shared secret); HKDF-SHA256 gives one AES-256-GCM key per direction. Frames are base64 lines of
/// ciphertext||tag with an implicit counter nonce (direction ‖ 64-bit counter), so a changed, dropped, replayed or reordered
/// frame — or a plain-text line — fails to decrypt and ends the run. Matches samples/addons/sdk/DcxBridge.cs.
/// </summary>
public sealed class AddonBridge : IDisposable
{
    public const string Info = "dcxaddon-bridge-2|";
    private static readonly byte[] Aad = Encoding.ASCII.GetBytes("dcxaddon-bridge-2");
    private readonly ECDiffieHellman _ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
    private readonly byte[] _salt = RandomNumberGenerator.GetBytes(32);
    private AesGcm? _send, _recv;
    private ulong _sendN, _recvN;

    public string PublicKey { get; }

    public AddonBridge()
    {
        var q = _ecdh.ExportParameters(false).Q;
        PublicKey = Convert.ToBase64String(q.X!.Concat(q.Y!).ToArray());
    }

    /// <summary>The app's plain-text hello line.</summary>
    /// <remarks>Written by hand: System.Text.Json would escape the '+' of base64 as +, which the SDK's small parser doesn't read.</remarks>
    public string Hello() => $"{{\"dcx\":\"hello\",\"api\":2,\"epk\":\"{PublicKey}\",\"salt\":\"{Convert.ToBase64String(_salt)}\"}}";

    /// <summary>Completes the handshake from the add-on's hello line. Throws <see cref="InvalidDataException"/> when it isn't one.</summary>
    public void Accept(string addonHello)
    {
        string epk;
        try { epk = (JsonNode.Parse(addonHello) as JsonObject)?["dcx"]?.GetValue<string>() == "hello" ? JsonNode.Parse(addonHello)!["epk"]!.GetValue<string>() : ""; }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or NullReferenceException) { epk = ""; }
        byte[] xy;
        try { xy = Convert.FromBase64String(epk); } catch (FormatException) { xy = Array.Empty<byte>(); }
        if (xy.Length != 64) throw new InvalidDataException("the add-on did not answer the encrypted handshake (API 2): it may be an old, unencrypted add-on");
        byte[] ikm;
        try
        {
            using var peer = ECDiffieHellman.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = xy[..32], Y = xy[32..] } });
            ikm = _ecdh.DeriveKeyFromHash(peer.PublicKey, HashAlgorithmName.SHA256);
        }
        catch (CryptographicException) { throw new InvalidDataException("the add-on's handshake key is not a valid P-256 point"); }
        var okm = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 64, _salt, Encoding.ASCII.GetBytes(Info + PublicKey + "|" + epk));
        _send = new AesGcm(okm[..32], 16);
        _recv = new AesGcm(okm[32..], 16);
        CryptographicOperations.ZeroMemory(ikm);
        CryptographicOperations.ZeroMemory(okm);
    }

    private static byte[] Nonce(byte direction, ulong n)
    {
        var b = new byte[12];
        b[3] = direction;
        for (int i = 0; i < 8; i++) b[11 - i] = (byte)(n >> (8 * i));
        return b;
    }

    /// <summary>One message to the add-on, as the line to write.</summary>
    public string Seal(string json)
    {
        var plain = Encoding.UTF8.GetBytes(json);
        var frame = new byte[plain.Length + 16];
        _send!.Encrypt(Nonce(0, _sendN++), plain, frame.AsSpan(0, plain.Length), frame.AsSpan(plain.Length), Aad);
        return Convert.ToBase64String(frame);
    }

    /// <summary>One line from the add-on, decrypted. Throws <see cref="InvalidDataException"/> for anything that isn't the next frame.</summary>
    public string Open(string line)
    {
        byte[] frame;
        try { frame = Convert.FromBase64String(line.Trim()); } catch (FormatException) { throw new InvalidDataException("a plain-text line on the encrypted bridge"); }
        if (frame.Length < 16) throw new InvalidDataException("a frame shorter than its tag");
        var plain = new byte[frame.Length - 16];
        try { _recv!.Decrypt(Nonce(1, _recvN), frame.AsSpan(0, plain.Length), frame.AsSpan(plain.Length), plain, Aad); }
        catch (CryptographicException) { throw new InvalidDataException("a frame that doesn't decrypt (changed, replayed, reordered or not encrypted)"); }
        _recvN++;
        return Encoding.UTF8.GetString(plain);
    }

    public void Dispose() { _ecdh.Dispose(); _send?.Dispose(); _recv?.Dispose(); }
}
