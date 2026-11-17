using Bloomberglp.Blpapi;
using Blp.Eqs;

namespace Blp.Tests.Eqs;

/// <summary>
/// <see cref="EqsHandler"/> over excerpts of responses a terminal sent, on the real
/// service shape.
/// </summary>
public class EqsCaptureTests
{
    /// <summary>
    /// With no options CreateRequest sends a GLOBAL screen in group General, and an
    /// empty overrides array.
    /// </summary>
    /// <remarks>
    /// Mutation: another default type or group, or an override sent with no as-of date.
    /// Oracle: the printed request of the eqs capture.
    /// </remarks>
    [Fact]
    public void CreateRequestMatchesTheCapturedRequest()
    {
        var handler = new EqsHandler(new EqsRequest("Core Capital Ratios"));

        string printed = CapturedRefDataEvents.PrintedText(
            handler.CreateRequest(CapturedRefDataEvents.Service));

        Assert.Equal(
            """
            BeqsRequest = {
                screenName = "Core Capital Ratios"
                screenType = GLOBAL
                Group = "General"
                overrides[] = {
                }
            }
            """,
            printed);
    }

    /// <summary>
    /// A responseError, for a screen that returns no data or one that does not exist,
    /// raises ResponseException with the error's category and its text.
    /// </summary>
    /// <remarks>
    /// Mutation: the responseError ignored, which throws on the missing data element
    /// instead, or the category read from the source element.
    /// Oracle: the responseError of the eqs capture (4080) and of the
    /// eqs_unknown_screen capture (4110).
    /// </remarks>
    [Theory]
    [InlineData("sendRequest", 4080, "apieqs", "madiResponse does not contain data")]
    [InlineData(
        "createBeqsMadiRequest",
        4110,
        "eqsscreenutils",
        "Cannot find screen, returnCode=e_SCREEN_NOT_FOUND")]
    public void ResponseErrorRaisesWithItsCategory(
        string source, int code, string category, string message)
    {
        var handler = new EqsHandler(new EqsRequest("Core Capital Ratios"));
        var responseEvent = CapturedRefDataEvents.Response(
            "BeqsRequest",
            Event.EventType.RESPONSE,
            $"""
            <BeqsResponse>
              <responseError>
                <source>{source}</source>
                <code>{code}</code>
                <category>{category}</category>
                <message>{message}</message>
              </responseError>
            </BeqsResponse>
            """);

        var error = Assert.Throws<ResponseException>(() => handler.ProcessResponse(responseEvent));

        Assert.Equal(category, error.Category);
        Assert.Null(error.Subcategory);
        Assert.StartsWith("REQUEST FAILED: ", error.Message);
        Assert.Contains(message, error.Message);
        Assert.Contains($"{code}", error.Message);
    }
}
