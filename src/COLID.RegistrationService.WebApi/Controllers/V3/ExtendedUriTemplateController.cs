﻿using System.Net.Mime;
using COLID.RegistrationService.Common.DataModel.ExtendedUriTemplates;
using COLID.RegistrationService.Services.Authorization.Requirements;
using COLID.RegistrationService.Services.Interface;
using COLID.RegistrationService.WebApi.Filters;
using COLID.StatisticsLog.Type;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Threading.Tasks;
using COLID.Identity.Requirements;

namespace COLID.RegistrationService.WebApi.Controllers.V3
{
    /// <summary>
    /// API endpoint for extended uri templates.
    /// </summary>
    [ApiController]
    [Authorize]
    [ApiVersion(Constants.API.Version.V3)]
    [Route("api/v{version:apiVersion}")]
    [Produces(MediaTypeNames.Application.Json)]
    public class ExtendedUriTemplateController : Controller
    {
        private readonly IExtendedUriTemplateService _extendedUriTemplateService;

        /// <summary>
        /// API endpoint for extended uri templates.
        /// </summary>
        /// <param name="extendedUriTemplateService">The service for extended uri templates</param>
        public ExtendedUriTemplateController(IExtendedUriTemplateService extendedUriTemplateService)
        {
            _extendedUriTemplateService = extendedUriTemplateService;
        }

        /// <summary>
        /// Returns a list of all created extended uri templates.
        /// </summary>
        /// <returns>A list of all created extended uri templates</returns>
        /// <response code="200">Returns the list of extended uri templates</response>
        /// <response code="500">If an unexpected error occurs</response>
        [HttpGet]
        [Route("extendedUriTemplateList")]
        public IActionResult GetExtendedUriTemplates()
        {
            var extendedUriTemplates = _extendedUriTemplateService.GetEntities(null);

            return Ok(extendedUriTemplates);
        }

        /// <summary>
        /// Returns the extended uri template of the given Id.
        /// </summary>
        /// <param name="id">The Id of a extended uri template.</param>
        /// <returns>A extended uri template</returns>
        /// <response code="200">Returns the extended uri template of the given Id</response>
        /// <response code="404">If no extended uri template exists with the given Id</response>
        /// <response code="500">If an unexpected error occurs</response>
        [HttpGet]
        [ValidateActionParameters]
        [Route("extendedUriTemplate")]
        public IActionResult GetExtendedUriTemplateById([FromQuery] string id)
        {
            var extendedUriTemplate = _extendedUriTemplateService.GetEntity(id);

            return Ok(extendedUriTemplate);
        }

        /// <summary>
        /// Creates an extended uri template.
        /// </summary>
        /// <param name="extendedUriTemplate">The new extended uri template to create</param>
        /// <returns>A newly created extended uri template</returns>
        /// <response code="201">Returns the newly created extended uri template</response>
        /// <response code="400">If the extendedUriTemplate is invalid</response>
        /// <response code="500">If an unexpected error occurs</response>
        [HttpPost]
        [Route("extendedUriTemplate")]
        [Authorize(Policy = nameof(AdministratorRequirement))]
        [Log(LogType.AuditTrail)]
        public async Task<IActionResult> CreateExtendedUriTemplate([FromBody] ExtendedUriTemplateRequestDTO extendedUriTemplate)
        {
            // Create extended uri template
            var result = await _extendedUriTemplateService.CreateEntity(extendedUriTemplate);

            //ToDo: Insert get route for created extendedUriTemplate
            return Created("/api/extendedUriTemplate?id=" + result.Entity.Id, result);
        }

        /// <summary>
        /// Edits the extended uri template with the given Id and sets the given values.
        /// </summary>
        /// <param name="id">The Id of the extended uri template to edit.</param>
        /// <param name="extendedUriTemplate">All values for the existing extended uri template</param>
        /// <returns>A status code</returns>
        /// <response code="200">Returns status code only</response>
        /// <response code="400">If the given Id or extended uri template information is invalid and do not match</response>
        /// <response code="500">If an unexpected error occurs</response>
        [HttpPut]
        [ValidateActionParameters]
        [Route("extendedUriTemplate")]
        [Authorize(Policy = nameof(AdministratorRequirement))]
        [Log(LogType.AuditTrail)]
        public IActionResult EditExtendedUriTemplate([FromQuery] string id, [FromBody] ExtendedUriTemplateRequestDTO extendedUriTemplate)
        {
            var newExtendedUriTemplate = _extendedUriTemplateService.EditEntity(id, extendedUriTemplate);

            return Ok();
        }

        /// <summary>
        /// Deletes an extended uri template.
        /// </summary>
        /// <remarks>
        /// </remarks>
        /// <param name="id">The Id of the extended uri template to delete.</param>
        /// <returns>A status code</returns>
        /// <response code="200">Returns status code only</response>
        /// <response code="500">If an unexpected error occurs</response>
        [HttpDelete]
        [ValidateActionParameters]
        [Route("extendedUriTemplate")]
        [Authorize(Policy = nameof(AdministratorRequirement))]
        [Log(LogType.AuditTrail)]
        public IActionResult DeleteExtendedUriTemplate([FromQuery] string id)
        {
            _extendedUriTemplateService.DeleteEntity(id);

            return Ok();
        }
    }
}
