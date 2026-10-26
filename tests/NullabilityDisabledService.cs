#nullable disable

namespace DynamicHttp.Tests;

// This type deliberately lives in a nullable-oblivious context. It guards the rule that a
// missing required value must produce a 400 even when the declaring assembly does not use
// nullable reference annotations (where NullabilityInfoContext reports "Unknown").
[HttpService("/api/nullability-disabled")]
public sealed class NullabilityDisabledService
{
    [HttpGet("/missing")]
    public string Get([FromQuery] string value) => value;
}

#nullable restore
