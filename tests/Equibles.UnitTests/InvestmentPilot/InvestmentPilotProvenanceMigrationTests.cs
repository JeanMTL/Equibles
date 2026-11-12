using Equibles.Migrations.Migrations;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace Equibles.UnitTests.InvestmentPilot;

public sealed class InvestmentPilotProvenanceMigrationTests
{
    [Fact]
    public void Up_AddsOnlyNullableProvenanceColumns()
    {
        var builder = new MigrationBuilder("Npgsql");
        var up = typeof(Migration).GetMethod(
            "Up",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic
        );

        up.Should().NotBeNull();
        up!.Invoke(new InvestmentPilotFactProvenance(), [builder]);

        builder.Operations.Should().HaveCount(2);
        builder.Operations.Should().OnlyContain(operation => operation is AddColumnOperation);
        builder.Operations.Cast<AddColumnOperation>().Select(operation => operation.Name)
            .Should().BeEquivalentTo("SourceCik", "SourceUrl");
        builder.Operations.Cast<AddColumnOperation>().Should().OnlyContain(operation =>
            operation.Table == "FinancialFact" && operation.IsNullable);
    }

    [Fact]
    public void Down_DropsOnlyTheTwoNullableProvenanceColumns()
    {
        var builder = new MigrationBuilder("Npgsql");
        var down = typeof(Migration).GetMethod(
            "Down",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic
        );

        down.Should().NotBeNull();
        down!.Invoke(new InvestmentPilotFactProvenance(), [builder]);

        builder.Operations.Should().HaveCount(2);
        builder.Operations.Should().OnlyContain(operation => operation is DropColumnOperation);
        builder.Operations.Cast<DropColumnOperation>().Select(operation => operation.Name)
            .Should().BeEquivalentTo("SourceCik", "SourceUrl");
        builder.Operations.Cast<DropColumnOperation>().Should().OnlyContain(operation =>
            operation.Table == "FinancialFact");
    }
}
