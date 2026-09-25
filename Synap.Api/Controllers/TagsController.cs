using MediatR;
using Microsoft.AspNetCore.Mvc;
using SergioIzq.AspNetCore.Kernel.Controllers;
using Synap.Application.Features.Notes.Queries;

namespace Synap.Api.Controllers;

[ApiController]
[Route("api/tags")]
public class TagsController : AbsController
{
    public TagsController(ISender sender) : base(sender)
    {
    }

    [HttpGet]
    public async Task<IActionResult> List()
        => await SendAndHandleAsync(new ListTagsQuery());
}
