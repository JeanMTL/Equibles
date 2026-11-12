using Equibles.CommonStocks.Data.Models;
using Equibles.Errors.BusinessLogic;
using Equibles.Integrations.Sec.Contracts;
using Equibles.Integrations.Sec.Models.Responses;
using Equibles.Sec.FinancialFacts.HostedService.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Equibles.UnitTests.Sec;

public sealed class FinancialFactsImportProvidedFactsTests
{
    [Fact]
    public async Task ImportProvidedFacts_RejectsMismatchedCikBeforeAnyIo()
    {
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var secClient = Substitute.For<ISecEdgarClient>();
        var errorReporter = Substitute.For<ErrorReporter>(
            scopeFactory,
            Substitute.For<ILogger<ErrorReporter>>()
        );
        var importer = new FinancialFactsImportService(
            scopeFactory,
            secClient,
            Substitute.For<ILogger<FinancialFactsImportService>>(),
            errorReporter
        );
        var issuer = new EquityIssuer { Cik = "0000123456" };
        var response = new CompanyFactsResponse
        {
            Cik = 789019,
            EntityName = "synthetic test issuer",
        };

        var act = () => importer.ImportProvidedFacts(issuer, response, CancellationToken.None);

        await act.Should()
            .ThrowAsync<InvalidDataException>()
            .WithMessage("Company Facts CIK 789019 does not match issuer CIK 0000123456.");
        scopeFactory.DidNotReceive().CreateScope();
        await secClient.DidNotReceiveWithAnyArgs().GetCompanyFacts(default);
    }
}
