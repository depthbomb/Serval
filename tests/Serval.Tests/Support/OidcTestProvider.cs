using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Serval.Tests.Support;

internal sealed class OidcTestProvider : HttpMessageHandler
{
    internal int                                                                     DiscoveryCalls;
    internal int                                                                     KeyCalls;
    internal int                                                                     TokenCalls;
    internal int                                                                     UserInfoCalls;
    internal RSA                                                                     Key            { get; private set; } = RSA.Create(2048);
    internal ECDsa?                                                                  EcKey          { get; set; }
    internal string                                                                  KeyId          { get; set; } = "first";
    internal string                                                                  Algorithm      { get; set; } = "RS256";
    internal string                                                                  Nonce          { get; set; } = "";
    internal DateTimeOffset                                                          Now            { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    internal Action<Dictionary<string, object?>>?                                    ChangeClaims   { get; set; }
    internal Action<Dictionary<string, object?>>?                                    ChangeHeader   { get; set; }
    internal Action<Dictionary<string, object?>>?                                    ChangeMetadata { get; set; }
    internal Func<string, string>?                                                   ChangeToken    { get; set; }
    internal Func<HttpRequestMessage, HttpResponseMessage?>?                         Respond        { get; set; }
    internal Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? RespondAsync   { get; set; }
    internal string                                                                  UserSubject    { get; set; } = "subject";
    internal string?                                                                 TokenBody      { get; private set; }

    internal void Rotate(bool sameId = false)
    {
        Key.Dispose();
        Key = RSA.Create(2048);
        if (!sameId)
        {
            KeyId = Guid.NewGuid().ToString("N");
        }
    }

    internal static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    internal static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json")
    };

    private string Token()
    {
        var header = new Dictionary<string, object?>
        {
            ["alg"] = Algorithm,
            ["kid"] = KeyId,
            ["typ"] = "JWT"
        };
        var claims = new Dictionary<string, object?>
        {
            ["iss"]   = "https://provider.test/tenant",
            ["sub"]   = "subject",
            ["aud"]   = "client",
            ["nonce"] = Nonce,
            ["iat"]   = Now.ToUnixTimeSeconds(),
            ["exp"]   = Now.AddMinutes(5).ToUnixTimeSeconds(),
            ["name"]  = "Ada",
        };
        ChangeClaims?.Invoke(claims);
        ChangeHeader?.Invoke(header);
        var unsigned = Encode(JsonSerializer.SerializeToUtf8Bytes(header)) + "." + Encode(JsonSerializer.SerializeToUtf8Bytes(claims));
        var hash     = Algorithm[2..] switch { "256" => HashAlgorithmName.SHA256, "384" => HashAlgorithmName.SHA384, _ => HashAlgorithmName.SHA512 };
        var bytes    = Encoding.ASCII.GetBytes(unsigned);
        var signature = EcKey is null
            ? Key.SignData(bytes, hash, Algorithm.StartsWith("PS", StringComparison.Ordinal) ? RSASignaturePadding.Pss : RSASignaturePadding.Pkcs1)
            : EcKey.SignData(bytes, hash, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return unsigned + "." + Encode(signature);
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (RespondAsync is not null)
        {
            return await RespondAsync(request, ct);
        }

        if (Respond?.Invoke(request) is {} response)
        {
            return response;
        }

        switch (request.RequestUri!.AbsolutePath)
        {
            case "/tenant/.well-known/openid-configuration":
                Interlocked.Increment(ref DiscoveryCalls);
                var metadata = new Dictionary<string, object?>
                {
                    ["issuer"]                                = "https://provider.test/tenant",
                    ["authorization_endpoint"]                = "https://provider.test/authorize",
                    ["token_endpoint"]                        = "https://provider.test/token",
                    ["userinfo_endpoint"]                     = "https://provider.test/userinfo",
                    ["jwks_uri"]                              = "https://provider.test/keys",
                    ["response_types_supported"]              = new[] { "code" },
                    ["subject_types_supported"]               = new[] { "public" },
                    ["id_token_signing_alg_values_supported"] = new[] { Algorithm },
                };
                ChangeMetadata?.Invoke(metadata);
                return Json(metadata);
            case "/keys":
                Interlocked.Increment(ref KeyCalls);
                Dictionary<string, object?> key;
                if (EcKey is not null)
                {
                    var ec = EcKey.ExportParameters(false);
                    key = new()
                    {
                        ["kty"] = "EC",
                        ["crv"] = Algorithm switch { "ES256" => "P-256", "ES384" => "P-384", _ => "P-521" },
                        ["x"]   = Encode(ec.Q.X!),
                        ["y"]   = Encode(ec.Q.Y!),
                    };
                }
                else
                {
                    var rsa = Key.ExportParameters(false);
                    key = new()
                    {
                        ["kty"] = "RSA",
                        ["n"]   = Encode(rsa.Modulus!),
                        ["e"]   = Encode(rsa.Exponent!)
                    };
                }
                key["kid"] = KeyId;
                key["use"] = "sig";
                key["alg"] = Algorithm;
                return Json(new
                {
                    keys = new[] { key }
                });
            case "/token":
                Interlocked.Increment(ref TokenCalls);
                TokenBody = await request.Content!.ReadAsStringAsync(ct);
                var token = Token();
                return Json(new
                {
                    access_token = "access-secret",
                    token_type   = "Bearer",
                    id_token     = ChangeToken?.Invoke(token) ?? token
                });
            case "/userinfo":
                Interlocked.Increment(ref UserInfoCalls);
                return Json(new
                {
                    sub  = UserSubject,
                    name = "UserInfo name"
                });
            default:
                throw new InvalidOperationException("Unexpected test request: " + request.RequestUri);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Key.Dispose();
            EcKey?.Dispose();
        }
        base.Dispose(disposing);
    }
}
