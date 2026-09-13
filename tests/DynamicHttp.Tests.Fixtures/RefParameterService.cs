namespace DynamicHttp.Tests.Fixtures;

[HttpService("/api/ref")]
public sealed class RefParameterService
{
    [HttpGet]
    public string Get(ref int value) => value.ToString();
}