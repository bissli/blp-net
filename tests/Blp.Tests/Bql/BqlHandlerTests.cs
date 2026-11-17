using Bloomberglp.Blpapi;
using Blp.Bql;

namespace Blp.Tests.Bql;

public class BqlHandlerTests
{
    private static readonly BqlRequest _request = new("get(px_last) for(['IBM US Equity'])");

    private static BqlResponse Process(params Event[] responseEvents)
    {
        var handler = new BqlHandler(_request);
        foreach (var responseEvent in responseEvents)
        {
            handler.ProcessResponse(responseEvent);
        }

        return handler.ToResponse();
    }

    /// <summary>CreateRequest carries the expression and the EXCEL app name.</summary>
    /// <remarks>
    /// Mutation: the expression dropped or sent in another element, or appName left
    /// out of clientContext.
    /// Oracle: the sendQuery request of //blp/bqlsvc, read back from the built request.
    /// </remarks>
    [Fact]
    public void CreateRequestCarriesExpressionAndAppName()
    {
        var query = new BqlHandler(_request)
            .CreateRequest(BqlEvents.Service)
            .AsElement;

        Assert.Equal(
            "get(px_last) for(['IBM US Equity'])",
            query.GetElementAsString(Name.GetName("expression")));
        var context = query.GetElement(Name.GetName("clientContext"));
        Assert.Equal("EXCEL", context.GetElementAsString(Name.GetName("appName")));
    }

    /// <summary>
    /// A result becomes one row per ID, holding ID, the item-named value and each
    /// secondary column, typed by column.
    /// </summary>
    /// <remarks>
    /// Mutation: the value keyed VALUE in place of the result name, a DATE kept as
    /// text or as a date-time, the columns in another order, or a row dropped.
    /// Oracle: a two-security excerpt of a px_last response, worked out by hand.
    /// </remarks>
    [Fact]
    public void ResultBecomesTypedRows()
    {
        var response = Process(BqlEvents.Result("""
            {"results":{"px_last":{
              "idColumn":{"name":"ID","type":"STRING","values":["IBM US Equity","AAPL US Equity"]},
              "valuesColumn":{"name":"VALUE","type":"DOUBLE",
                              "values":[232.8800048828125,350.6099853515625]},
              "secondaryColumns":[
                {"name":"DATE","type":"DATE",
                 "values":["2026-11-17T00:00:00Z","2026-11-17T00:00:00Z"]},
                {"name":"CURRENCY","type":"STRING","values":["USD","USD"]}]}},
              "responseExceptions":null}
            """));

        var rows = Assert.Single(response.Results, pair => pair.Key == "px_last").Value;
        Assert.Equal(2, rows.Count);
        Assert.Equal(["ID", "px_last", "DATE", "CURRENCY"], rows[0].Keys);
        Assert.Equal("IBM US Equity", rows[0]["ID"]);
        Assert.Equal(232.8800048828125, rows[0]["px_last"]);
        Assert.Equal(new DateOnly(2026, 11, 17), rows[0]["DATE"]);
        Assert.Equal("USD", rows[0]["CURRENCY"]);
        Assert.Equal("AAPL US Equity", rows[1]["ID"]);
        Assert.Equal(350.6099853515625, rows[1]["px_last"]);
        Assert.Equal(new DateOnly(2026, 11, 17), rows[1]["DATE"]);
        Assert.Empty(response.Errors);
    }

    /// <summary>INT, BOOLEAN and any other type become long, bool and string.</summary>
    /// <remarks>
    /// Mutation: an INT read as double, a BOOLEAN kept as text, or an unlisted type
    /// raising in place of a string.
    /// Oracle: hand-written columns, one per type.
    /// </remarks>
    [Fact]
    public void IntBooleanAndOtherTypesConvert()
    {
        var response = Process(BqlEvents.Result("""
            {"results":{"item":{
              "idColumn":{"name":"ID","type":"STRING","values":["IBM US Equity"]},
              "valuesColumn":{"name":"VALUE","type":"INT","values":[7]},
              "secondaryColumns":[
                {"name":"FLAG","type":"BOOLEAN","values":[true]},
                {"name":"OTHER","type":"DATETIME","values":["2026-11-17T09:30:00Z"]}]}}}
            """));

        var row = Assert.Single(response.Results["item"]);
        Assert.Equal(7L, row["item"]);
        Assert.Equal(true, row["FLAG"]);
        Assert.Equal("2026-11-17T09:30:00Z", row["OTHER"]);
    }

    /// <summary>
    /// A value that does not fit its column type comes back as sent, and a missing
    /// secondaryColumns gives rows of ID and the value only.
    /// </summary>
    /// <remarks>
    /// Mutation: a mismatched value throwing or becoming null, a number under an
    /// unlisted type read as a string, or a missing secondaryColumns throwing.
    /// Oracle: hand-written columns that break each type rule once.
    /// </remarks>
    [Fact]
    public void MismatchedValuesComeBackRawAndMissingColumnsAreEmpty()
    {
        var mismatched = Process(BqlEvents.Result("""
            {"results":{"item":{
              "idColumn":{"name":"ID","type":"STRING","values":["A","B"]},
              "valuesColumn":{"name":"VALUE","type":"DOUBLE","values":["Infinity",1.5]},
              "secondaryColumns":[
                {"name":"COUNT","type":"INT","values":[2.5,3]},
                {"name":"ODD","type":"OTHER","values":[4,"x"]},
                {"name":"DATE","type":"DATE","values":["soon","2026-11-17"]}]}}}
            """));
        var bare = Process(BqlEvents.Result("""
            {"results":{"item":{
              "idColumn":{"name":"ID","type":"STRING","values":["A"]},
              "valuesColumn":{"name":"VALUE","type":"DOUBLE","values":[1.5]}}}}
            """));

        var rows = mismatched.Results["item"];
        Assert.Equal("Infinity", rows[0]["item"]);
        Assert.Equal(2.5, rows[0]["COUNT"]);
        Assert.Equal(3L, rows[1]["COUNT"]);
        Assert.Equal(4.0, rows[0]["ODD"]);
        Assert.Equal("soon", rows[0]["DATE"]);
        Assert.Equal(new DateOnly(2026, 11, 17), rows[1]["DATE"]);
        var row = Assert.Single(bare.Results["item"]);
        Assert.Equal(["ID", "item"], row.Keys);
    }

    /// <summary>A secondary column named like an earlier one overwrites it.</summary>
    /// <remarks>
    /// Mutation: the row built with Add, which throws on the repeated key.
    /// Oracle: a hand-written secondary column named ID.
    /// </remarks>
    [Fact]
    public void LaterColumnOfTheSameNameOverwrites()
    {
        var response = Process(BqlEvents.Result("""
            {"results":{"item":{
              "idColumn":{"name":"ID","type":"STRING","values":["A"]},
              "valuesColumn":{"name":"VALUE","type":"DOUBLE","values":[1.5]},
              "secondaryColumns":[{"name":"ID","type":"STRING","values":["B"]}]}}}
            """));

        var row = Assert.Single(response.Results["item"]);
        Assert.Equal("B", row["ID"]);
    }

    /// <summary>A JSON null and the text "NaN" become null in any column.</summary>
    /// <remarks>
    /// Mutation: NaN kept as text, a null value raising, or NaN handled only in the
    /// value column.
    /// Oracle: a hand-written excerpt with null and "NaN" in a DOUBLE and a STRING
    /// column.
    /// </remarks>
    [Fact]
    public void NullAndNanBecomeNull()
    {
        var response = Process(BqlEvents.Result("""
            {"results":{"px_last":{
              "idColumn":{"name":"ID","type":"STRING","values":["A","B","C"]},
              "valuesColumn":{"name":"VALUE","type":"DOUBLE","values":[244.67,null,"NaN"]},
              "secondaryColumns":[
                {"name":"CURRENCY","type":"STRING","values":["USD","NaN",null]}]}}}
            """));

        var rows = response.Results["px_last"];
        Assert.Equal(244.67, rows[0]["px_last"]);
        Assert.Null(rows[1]["px_last"]);
        Assert.Null(rows[2]["px_last"]);
        Assert.Equal("USD", rows[0]["CURRENCY"]);
        Assert.Null(rows[1]["CURRENCY"]);
        Assert.Null(rows[2]["CURRENCY"]);
    }

    /// <summary>
    /// Two results of one message stay apart, keyed by the name Bloomberg sends, in
    /// arrival order.
    /// </summary>
    /// <remarks>
    /// Mutation: the results merged into one list, keyed by the item name without its
    /// parameters, or kept in sorted order.
    /// Oracle: an excerpt of a px_last range plus name response.
    /// </remarks>
    [Fact]
    public void TwoResultsStayApart()
    {
        var response = Process(BqlEvents.Result("""
            {"results":{
              "px_last(dates=range(-5d,0d))":{
                "idColumn":{"name":"ID","type":"STRING","values":["IBM US Equity","IBM US Equity"]},
                "valuesColumn":{"name":"VALUE","type":"DOUBLE","values":[244.67,247.13]},
                "secondaryColumns":[
                  {"name":"DATE","type":"DATE",
                   "values":["2026-11-12T00:00:00Z","2026-11-13T00:00:00Z"]}]},
              "name":{
                "idColumn":{"name":"ID","type":"STRING","values":["IBM US Equity"]},
                "valuesColumn":{"name":"VALUE","type":"STRING",
                                "values":["International Business Machine"]},
                "secondaryColumns":[]}}}
            """));

        Assert.Equal(["px_last(dates=range(-5d,0d))", "name"], response.Results.Keys);
        var prices = response.Results["px_last(dates=range(-5d,0d))"];
        Assert.Equal(2, prices.Count);
        Assert.Equal(247.13, prices[1]["px_last(dates=range(-5d,0d))"]);
        var row = Assert.Single(response.Results["name"]);
        Assert.Equal(["ID", "name"], row.Keys);
        Assert.Equal("International Business Machine", row["name"]);
    }

    /// <summary>
    /// A non-empty responseExceptions fills Errors, with message, else
    /// internalMessage, else a fixed text, and the message's results are skipped.
    /// </summary>
    /// <remarks>
    /// Mutation: results kept beside the errors, only the first exception read, or
    /// internalMessage ignored.
    /// Oracle: an excerpt of an undefined-item response, with a hand-written second and
    /// third exception.
    /// </remarks>
    [Fact]
    public void ResponseExceptionsFillErrorsAndSkipResults()
    {
        var response = Process(BqlEvents.Result("""
            {"results":{"px_last":{
              "idColumn":{"name":"ID","type":"STRING","values":["A"]},
              "valuesColumn":{"name":"VALUE","type":"DOUBLE","values":[1.0]},
              "secondaryColumns":[]}},
             "responseExceptions":[
              {"message":"Error: Undefined item: not_a_field.","internalMessage":"scalar node"},
              {"internalMessage":"Error, scalar node found"},
              {}]}
            """));

        Assert.Empty(response.Results);
        Assert.Equal(
            [
                "Error: Undefined item: not_a_field.",
                "Error, scalar node found",
                "Unknown BQL error",
            ],
            response.Errors);
    }

    /// <summary>A result name in two messages appends its rows in order.</summary>
    /// <remarks>
    /// Mutation: the second message replacing the first, or its rows placed first.
    /// Oracle: a hand-split px_last result over a partial and a final response.
    /// </remarks>
    [Fact]
    public void ResultSplitOverTwoMessagesAppends()
    {
        string first = """
            {"results":{"px_last":{
              "idColumn":{"name":"ID","type":"STRING","values":["IBM US Equity"]},
              "valuesColumn":{"name":"VALUE","type":"DOUBLE","values":[232.55]},
              "secondaryColumns":[]}}}
            """;
        string second = """
            {"results":{"px_last":{
              "idColumn":{"name":"ID","type":"STRING","values":["AAPL US Equity"]},
              "valuesColumn":{"name":"VALUE","type":"DOUBLE","values":[351.28]},
              "secondaryColumns":[]}}}
            """;

        var response = Process(
            BqlEvents.Result(first, Event.EventType.PARTIAL_RESPONSE),
            BqlEvents.Result(second));

        var rows = response.Results["px_last"];
        Assert.Equal(["IBM US Equity", "AAPL US Equity"], rows.Select(row => row["ID"]));
        Assert.Equal([232.55, 351.28], rows.Select(row => row["px_last"]));
    }
}
