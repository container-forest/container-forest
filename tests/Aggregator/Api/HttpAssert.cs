using System.Net;

namespace ContainerForest.Aggregator.Api.Tests;

internal static class HttpAssert
{
    /// <summary>
    /// Like Assert.Equal on the status code, but a failure shows the actual status and the response body
    /// (usually a problem document), so a failed run explains itself without being reproduced.
    /// </summary>
    public static async Task StatusAsync(HttpStatusCode expected, HttpResponseMessage response)
    {
        if (response.StatusCode == expected)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Fail($"Expected {(int)expected} {expected}, got {(int)response.StatusCode} {response.StatusCode}. Body: {body}");
    }

    public static async Task StatusAsync(HttpStatusCode expected, Task<HttpResponseMessage> response) =>
        await StatusAsync(expected, await response);
}
