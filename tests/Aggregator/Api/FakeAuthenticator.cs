using System.Buffers.Text;
using System.Formats.Cbor;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ContainerForest.Aggregator.Api.Tests;

/// <summary>
/// A WebAuthn authenticator in software: answers <c>navigator.credentials.create()</c> and <c>get()</c> options the
/// way a browser would serialize them with <c>toJSON()</c>, with an ES256 key and "none" attestation.
/// </summary>
internal sealed class FakeAuthenticator(string origin = "http://localhost", string rpId = "localhost") : IDisposable
{
    private const byte UserPresent = 0x01;
    private const byte UserVerified = 0x04;
    private const byte AttestedCredentialData = 0x40;

    private readonly ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly byte[] credentialId = RandomNumberGenerator.GetBytes(32);
    private byte[]? userHandle;
    private uint signCount;

    public string CredentialId => Base64Url.EncodeToString(credentialId);

    public void Dispose() => key.Dispose();

    /// <summary>The credential for creation options from the aggregator.</summary>
    public string Create(string creationOptionsJson)
    {
        var options = JsonNode.Parse(creationOptionsJson)!;
        userHandle = Base64Url.DecodeFromChars(options["user"]!["id"]!.GetValue<string>());
        var clientData = ClientData("webauthn.create", options["challenge"]!.GetValue<string>());

        var parameters = key.ExportParameters(includePrivateParameters: false);
        var cose = new CborWriter(CborConformanceMode.Ctap2Canonical);
        cose.WriteStartMap(5);
        cose.WriteInt32(1);
        cose.WriteInt32(2); // kty: EC2
        cose.WriteInt32(3);
        cose.WriteInt32(-7); // alg: ES256
        cose.WriteInt32(-1);
        cose.WriteInt32(1); // crv: P-256
        cose.WriteInt32(-2);
        cose.WriteByteString(parameters.Q.X!);
        cose.WriteInt32(-3);
        cose.WriteByteString(parameters.Q.Y!);
        cose.WriteEndMap();

        var authData = new List<byte>(AuthenticatorData((byte)(UserPresent | UserVerified | AttestedCredentialData)));
        authData.AddRange(new byte[16]); // AAGUID
        authData.Add((byte)(credentialId.Length >> 8));
        authData.Add((byte)credentialId.Length);
        authData.AddRange(credentialId);
        authData.AddRange(cose.Encode());

        var attestation = new CborWriter(CborConformanceMode.Ctap2Canonical);
        attestation.WriteStartMap(3);
        attestation.WriteTextString("fmt");
        attestation.WriteTextString("none");
        attestation.WriteTextString("attStmt");
        attestation.WriteStartMap(0);
        attestation.WriteEndMap();
        attestation.WriteTextString("authData");
        attestation.WriteByteString([.. authData]);
        attestation.WriteEndMap();

        return Credential(new JsonObject
        {
            ["clientDataJSON"] = Base64Url.EncodeToString(clientData),
            ["attestationObject"] = Base64Url.EncodeToString(attestation.Encode()),
            ["transports"] = new JsonArray("internal"),
        });
    }

    /// <summary>The assertion for request options from the aggregator, signed with the key made by <see cref="Create"/>.</summary>
    public string Get(string requestOptionsJson)
    {
        var options = JsonNode.Parse(requestOptionsJson)!;
        var clientData = ClientData("webauthn.get", options["challenge"]!.GetValue<string>());
        var authData = AuthenticatorData((byte)(UserPresent | UserVerified));
        var signature = key.SignData([.. authData, .. SHA256.HashData(clientData)], HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

        return Credential(new JsonObject
        {
            ["clientDataJSON"] = Base64Url.EncodeToString(clientData),
            ["authenticatorData"] = Base64Url.EncodeToString(authData),
            ["signature"] = Base64Url.EncodeToString(signature),
            ["userHandle"] = Base64Url.EncodeToString(userHandle ?? throw new InvalidOperationException("Create a credential first.")),
        });
    }

    private byte[] ClientData(string type, string challenge) => Encoding.UTF8.GetBytes(
        JsonSerializer.Serialize(new { type, challenge, origin, crossOrigin = false }));

    private byte[] AuthenticatorData(byte flags)
    {
        signCount++;
        return [.. SHA256.HashData(Encoding.UTF8.GetBytes(rpId)), flags, (byte)(signCount >> 24), (byte)(signCount >> 16), (byte)(signCount >> 8), (byte)signCount];
    }

    private string Credential(JsonObject response) => new JsonObject
    {
        ["id"] = CredentialId,
        ["rawId"] = CredentialId,
        ["type"] = "public-key",
        ["response"] = response,
        ["clientExtensionResults"] = new JsonObject(),
        ["authenticatorAttachment"] = "platform",
    }.ToJsonString();
}
