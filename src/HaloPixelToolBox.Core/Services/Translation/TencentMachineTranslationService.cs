using TencentCloud.Common;
using TencentCloud.Common.Profile;
using TencentCloud.Tmt.V20180321;
using TencentCloud.Tmt.V20180321.Models;

namespace HaloPixelToolBox.Core.Services.Translation;

public sealed class TencentMachineTranslationService : ITranslationService
{
    private const string DefaultEndpoint = "tmt.tencentcloudapi.com";
    private const string DefaultRegion = "ap-shanghai";

    private readonly string endpoint;
    private readonly string region;
    private readonly string? configuredSecretId;
    private readonly string? configuredSecretKey;

    public TencentMachineTranslationService(string? endpoint = null, string? apiKey = null)
    {
        endpoint = string.IsNullOrWhiteSpace(endpoint) ? DefaultEndpoint : endpoint.Trim();
        var endpointParts = endpoint.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        this.endpoint = endpointParts.Length > 0 ? endpointParts[0] : DefaultEndpoint;
        region = endpointParts.Length > 1 ? endpointParts[1] : DefaultRegion;

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            var keyParts = apiKey.Split([':', '|', ','], 2, StringSplitOptions.TrimEntries);
            if (keyParts.Length == 2)
            {
                configuredSecretId = keyParts[0];
                configuredSecretKey = keyParts[1];
            }
        }
    }

    public Task<string> TranslateAsync(string text, string sourceLanguage, string targetLanguage, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Task.FromResult(string.Empty);

        var secretId = configuredSecretId ?? Environment.GetEnvironmentVariable("TENCENTCLOUD_SECRET_ID");
        var secretKey = configuredSecretKey ?? Environment.GetEnvironmentVariable("TENCENTCLOUD_SECRET_KEY");
        if (string.IsNullOrWhiteSpace(secretId) || string.IsNullOrWhiteSpace(secretKey))
            throw new InvalidOperationException("Tencent TMT credentials are missing. Set TENCENTCLOUD_SECRET_ID/TENCENTCLOUD_SECRET_KEY or fill SecretId:SecretKey in the API key field.");

        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var credential = new Credential
            {
                SecretId = secretId,
                SecretKey = secretKey
            };

            var clientProfile = new ClientProfile
            {
                HttpProfile = new HttpProfile
                {
                    Endpoint = endpoint
                }
            };

            var client = new TmtClient(credential, region, clientProfile);
            var request = new TextTranslateRequest
            {
                SourceText = text,
                Source = NormalizeTencentLanguage(sourceLanguage),
                Target = NormalizeTencentLanguage(targetLanguage),
                ProjectId = 0
            };

            var response = client.TextTranslateSync(request);
            return response.TargetText ?? string.Empty;
        }, cancellationToken);
    }

    private static string NormalizeTencentLanguage(string language)
    {
        var normalized = (language ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized) || normalized == "auto")
            return "auto";
        if (normalized.StartsWith("zh", StringComparison.Ordinal))
            return "zh";
        if (normalized.StartsWith("ja", StringComparison.Ordinal) || normalized.StartsWith("jp", StringComparison.Ordinal))
            return "ja";
        if (normalized.StartsWith("en", StringComparison.Ordinal))
            return "en";
        if (normalized.StartsWith("fr", StringComparison.Ordinal))
            return "fr";
        return normalized;
    }
}
