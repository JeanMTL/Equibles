using System.Net;
using System.Text;
using Equibles.InvestmentPilot;

namespace Equibles.UnitTests.InvestmentPilot;

public sealed class BoundedSecCompanyFactsSourceTests
{
    private const string ValidPayload = "{\"cik\":789019,\"entityName\":\"MICROSOFT CORPORATION\",\"facts\":{}}";

    [Fact]
    public async Task FetchMsftAsync_UsesOnlyTheFixedOfficialEndpoint_AndOneRequest()
    {
        var handler = new RecordingHandler(_ => JsonResponse(ValidPayload));
        var source = new BoundedSecCompanyFactsSource(new HttpClient(handler));

        var result = await source.FetchMsftAsync("owner@example.org", CancellationToken.None);

        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Method.Should().Be(HttpMethod.Get);
        handler.Requests[0].RequestUri!.AbsoluteUri.Should().Be(BoundedSecCompanyFactsSource.AllowedUrl);
        handler.Requests[0].Headers.UserAgent.ToString().Should().Contain("owner@example.org");
        result.RequestCount.Should().Be(1);
        result.Payload.Cik.Should().Be(789019);
        result.SourceUrl.AbsoluteUri.Should().Be(BoundedSecCompanyFactsSource.AllowedUrl);
        var secondCall = () => source.FetchMsftAsync("owner@example.org", CancellationToken.None);
        await secondCall.Should().ThrowAsync<InvalidOperationException>();
        handler.Requests.Should().ContainSingle();
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TemporaryRedirect)]
    public async Task FetchMsftAsync_StopsWithoutRetryOrRedirect(HttpStatusCode status)
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(status));
        var source = new BoundedSecCompanyFactsSource(new HttpClient(handler));

        var act = () => source.FetchMsftAsync("owner@example.org", CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>();
        handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task FetchMsftAsync_RejectsBadContactBeforeNetwork()
    {
        var handler = new RecordingHandler(_ => JsonResponse(ValidPayload));
        var source = new BoundedSecCompanyFactsSource(new HttpClient(handler));

        var act = () => source.FetchMsftAsync("not-an-email", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task FetchMsftAsync_RejectsMismatchedCik()
    {
        var handler = new RecordingHandler(_ => JsonResponse(ValidPayload.Replace("789019", "1017413")));
        var source = new BoundedSecCompanyFactsSource(new HttpClient(handler));

        var act = () => source.FetchMsftAsync("owner@example.org", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidDataException>().WithMessage("*CIK 0000789019*");
        handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task FetchMsftAsync_RejectsOversizedBody()
    {
        var body = new byte[checked((int)BoundedSecCompanyFactsSource.MaxResponseBytes + 1)];
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        var source = new BoundedSecCompanyFactsSource(new HttpClient(handler));

        var act = () => source.FetchMsftAsync("owner@example.org", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidDataException>().WithMessage("*exceeds*");
        handler.Requests.Should().ContainSingle();
    }

    private static HttpResponseMessage JsonResponse(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }
}
