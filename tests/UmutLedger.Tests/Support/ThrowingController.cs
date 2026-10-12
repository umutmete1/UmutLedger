using Microsoft.AspNetCore.Mvc;

namespace UmutLedger.Tests.Support;

[ApiController]
[Route("test/throw")]
public sealed class ThrowingController : ControllerBase
{
    public const string SecretMessage = "secret-internal-detail";

    [HttpGet]
    public IActionResult Throw() => throw new InvalidOperationException(SecretMessage);
}
