using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Equibles.Integrations.Sec.Models.Responses;
using Newtonsoft.Json;

namespace Equibles.InvestmentPilot;

public interface ISecCompanyFactsSource
{
    Task<SecCompanyFactsDownload> FetchMsftAsync(
        string contactEmail,
        CancellationToken cancellationToken
    );
}

public sealed record SecCompanyFactsDownload(
    CompanyFactsResponse Payload,
    Uri SourceUrl,
    int RequestCount,
    long DownloadedBodyBytes,
    DateTimeOffset RetrievedAtUtc,
    int ObservationCount
);

/// <summary>
/// One request to the single allowlisted SEC Company Facts URL. No redirects, archive paths,
/// retries, pagination, or caller-controlled CIK/URL are supported.
/// </summary>
public sealed class BoundedSecCompanyFactsSource(HttpClient httpClient) : ISecCompanyFactsSource
{
    public const string AllowedUrl =
        "https://data.sec.gov/api/xbrl/companyfacts/CIK0000789019.json";
    public const long MaxResponseBytes = 25 * 1024 * 1024;
    public const int MaxObservations = 100_000;
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(45);

    private static readonly Uri SourceUri = new(AllowedUrl, UriKind.Absolute);
    private int _requestStarted;

    public async Task<SecCompanyFactsDownload> FetchMsftAsync(
        string contactEmail,
        CancellationToken cancellationToken
    )
    {
        var safeEmail = SecContactEmail.Validate(contactEmail);
        if (Interlocked.Exchange(ref _requestStarted, 1) != 0)
            throw new InvalidOperationException("This source instance permits exactly one SEC request.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);

        using var request = new HttpRequestMessage(HttpMethod.Get, SourceUri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.AcceptEncoding.ParseAdd("identity");
        request.Headers.UserAgent.ParseAdd($"Equibles OSS OMNIA Investment Pilot/1.0 ({safeEmail})");
        request.Headers.ConnectionClose = true;

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token
            );
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"The single SEC request exceeded {RequestTimeout.TotalSeconds:0} seconds.");
        }

        using (response)
        {
            if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.Forbidden)
                throw new HttpRequestException(
                    $"SEC returned {(int)response.StatusCode}; the one-shot run stops without retry.",
                    null,
                    response.StatusCode
                );
            if ((int)response.StatusCode is >= 300 and < 400)
                throw new HttpRequestException("SEC redirects are not followed by the pilot.", null, response.StatusCode);
            response.EnsureSuccessStatusCode();

            if (response.Content.Headers.ContentType?.MediaType is not "application/json")
                throw new InvalidDataException("SEC Company Facts response was not application/json.");
            if (response.Content.Headers.ContentEncoding.Count > 0)
                throw new InvalidDataException("Compressed SEC responses are not accepted by the byte-bounded pilot.");
            if (response.Content.Headers.ContentLength > MaxResponseBytes)
                throw new InvalidDataException($"SEC response Content-Length exceeds {MaxResponseBytes} bytes.");

            var body = await ReadBoundedAsync(response.Content, timeout.Token);
            var payload = JsonConvert.DeserializeObject<CompanyFactsResponse>(Encoding.UTF8.GetString(body));
            if (payload is null || payload.Cik != 789019 || string.IsNullOrWhiteSpace(payload.EntityName))
                throw new InvalidDataException("SEC Company Facts payload identity does not match CIK 0000789019.");

            var observations = CountObservations(payload);
            if (observations > MaxObservations)
                throw new InvalidDataException($"SEC payload exceeds the {MaxObservations} observation ceiling.");

            return new SecCompanyFactsDownload(
                payload,
                SourceUri,
                RequestCount: 1,
                DownloadedBodyBytes: body.LongLength,
                RetrievedAtUtc: DateTimeOffset.UtcNow,
                ObservationCount: observations
            );
        }
    }

    public static HttpMessageHandler CreateHttpClientHandler() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        ConnectTimeout = TimeSpan.FromSeconds(10),
    };

    private static async Task<byte[]> ReadBoundedAsync(
        HttpContent content,
        CancellationToken cancellationToken
    )
    {
        await using var input = await content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var remaining = MaxResponseBytes - output.Length;
            var read = await input.ReadAsync(
                buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining + 1)),
                cancellationToken
            );
            if (read == 0)
                return output.ToArray();
            if (read > remaining)
                throw new InvalidDataException($"SEC response body exceeds {MaxResponseBytes} bytes.");
            output.Write(buffer, 0, read);
        }
    }

    private static int CountObservations(CompanyFactsResponse payload) => payload.Facts
        .Values
        .SelectMany(concepts => concepts.Values)
        .SelectMany(concept => concept.Units.Values)
        .Sum(values => values.Count);
}

public static class SecContactEmail
{
    public static string Validate(string email)
    {
        if (
            string.IsNullOrWhiteSpace(email)
            || email.Contains('\r')
            || email.Contains('\n')
            || !System.Net.Mail.MailAddress.TryCreate(email, out var address)
            || !string.Equals(address.Address, email.Trim(), StringComparison.OrdinalIgnoreCase)
        )
            throw new InvalidOperationException(
                "Set a genuine SEC contact address in the local Sec__ContactEmail environment variable before the one-shot run."
            );

        return address.Address;
    }
}
