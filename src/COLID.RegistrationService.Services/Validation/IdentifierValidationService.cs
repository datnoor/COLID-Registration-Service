﻿using System;
using System.Collections.Generic;
using System.Linq;
using COLID.Common.Extensions;
using COLID.RegistrationService.Repositories.Interface;
using COLID.RegistrationService.Services.Interface;
using COLID.RegistrationService.Services.Extensions;
using AutoMapper;
using COLID.Graph.Metadata.Services;
using COLID.Graph.TripleStore.DataModels.Base;
using COLID.Graph.Metadata.DataModels.Validation;
using COLID.Graph.Metadata.Extensions;
using COLID.RegistrationService.Common.DataModel.Validation;
using COLID.Graph.TripleStore.Extensions;
using COLID.RegistrationService.Common.DataModel.Resources;
using COLID.Graph.Metadata.DataModels.Resources;

namespace COLID.RegistrationService.Services.Validation
{
    internal class IdentifierValidationService : IIdentifierValidationService
    {
        private readonly IMetadataService _metadataService;
        private readonly IResourceRepository _resourceRepository;
        private readonly IIdentifierService _identifierService;

        private readonly string _cachePrefix;

        public IdentifierValidationService(IMetadataService metadataService, IIdentifierService identifierService, IResourceRepository resourceRepository)
        {
            _metadataService = metadataService;
            _identifierService = identifierService;
            _resourceRepository = resourceRepository;
        }

        #region Duplicate Check

        public IList<ValidationResultProperty> CheckDuplicates(Entity resource, string resourceId, string previousVersion)
        {
            var duplicateErrors = CheckDuplicatesInRepository(resource, resourceId, previousVersion);

            // Create a flatten list of all pid, base and target uris of the given resource
            var urls = GetIdentifierAndTargetUrls(resource);

            // Group the flatten lists by uris to find duplicates inside of this (not yet) saved/published resource
            var groupedPidUris = urls.GroupBy(r => r.Url).Where(r => r.Count() > 1);

            foreach (var group in groupedPidUris)
            {
                duplicateErrors = duplicateErrors.Concat(group.Select(r =>
                {
                    if (r.PropertyKey == Graph.Metadata.Constants.Resource.DistributionEndpoints.HasNetworkAddress)
                    {
                        return new ValidationResultProperty(r.EntityId, r.PropertyKey, group.Key, $"This Target URI is identical with {group.Count() - 1} other Target URI of this entry", ValidationResultSeverity.Info, ValidationResultPropertyType.DUPLICATE);
                    }
                    return new ValidationResultProperty(r.EntityId, r.PropertyKey, group.Key, $"This identifier is identical with {group.Count() - 1} other identifier of this entry", ValidationResultSeverity.Violation, ValidationResultPropertyType.DUPLICATE);

                }
                    )).ToList();
            }
            return duplicateErrors;
        }

        private IList<ValidationResultProperty> CheckDuplicatesInRepository(Entity resource, string resourceId, string previousVersion)
        {
            var duplicateErrors = new List<ValidationResultProperty>();

            var pidUriResults = GetDuplicateResultsByIdentifierType(Graph.Metadata.Constants.EnterpriseCore.PidUri, resource, out var pidUri);
            var baseUriResults = GetDuplicateResultsByIdentifierType(Graph.Metadata.Constants.Resource.BaseUri, resource, out var baseUri);
            var targetUriResults = GetDuplicateResultsByTargetUri(resource, out var targetUri);

            // TODO: Piduri has no entry then duiplicate, or if entry/entryDraft is not actual resource

            if (CheckIdentifierIsDuplicate(pidUriResults, resource, resourceId))
            {
                var pidUriValidationResult = new ValidationResultProperty(resource.Id, Graph.Metadata.Constants.EnterpriseCore.PidUri, pidUri, Common.Constants.Validation.DuplicateField, ValidationResultSeverity.Violation, ValidationResultPropertyType.DUPLICATE);
                duplicateErrors.Add(pidUriValidationResult);
            }

            if (CheckBaseUriIsDuplicate(baseUriResults, resource, resourceId, pidUri, previousVersion))
            {
                var baseUriValidatioResult = new ValidationResultProperty(resource.Id, Graph.Metadata.Constants.Resource.BaseUri, baseUri, Common.Constants.Validation.DuplicateField, ValidationResultSeverity.Violation, ValidationResultPropertyType.DUPLICATE, null);
                duplicateErrors.Add(baseUriValidatioResult);
            }

            if (CheckIdentifierIsDuplicate(targetUriResults, resource, resourceId))
            {
                var targetUriValidationResult = new ValidationResultProperty(resource.Id, Graph.Metadata.Constants.Resource.DistributionEndpoints.HasNetworkAddress, targetUri, Common.Constants.Validation.DuplicateField, ValidationResultSeverity.Info,  ValidationResultPropertyType.DUPLICATE);
                duplicateErrors.Add(targetUriValidationResult);
            }

            foreach (var property in resource.Properties)
            {
                foreach (var prop in property.Value)
                {
                    if (DynamicExtension.IsType<Entity>(prop, out Entity parsedProp) && property.Key != Graph.Metadata.Constants.EnterpriseCore.PidUri && property.Key != Graph.Metadata.Constants.Resource.BaseUri)
                    {
                        duplicateErrors.AddRange(CheckDuplicatesInRepository(parsedProp, resourceId, string.Empty));
                    }
                }
            }

            return duplicateErrors;
        }

        /// <summary>
        /// Checks if an identifier in format of an uri is unique. 
        /// The base uri is a special kind of identifier and is therefore checked separately.
        /// </summary>
        /// <param name="pidUriResults">All found entries with the same identifier</param>
        /// <param name="resource">Id of the entity to be checked</param>
        /// <param name="resourceId">Resource id of the main entry (parent entry id)</param>
        /// <returns></returns>
        private bool CheckIdentifierIsDuplicate(IList<DuplicateResult> pidUriResults, Entity resource, string resourceId)
        {
            string resourceType = resource.Properties.GetValueOrNull(Graph.Metadata.Constants.RDF.Type, true);

            if (pidUriResults.IsNullOrEmpty())
            {
                return false;
            }
            else if (pidUriResults.IsAnyDraftAndPublishedNull())
            {
                return true;
            }
            else if (string.IsNullOrWhiteSpace(resourceId))
            {
                return true;
            }
            else if (pidUriResults.IsAnyDraftAndPublishedNotEqualToIdentifier(resourceId))
            {
                return true;
            }
            else if (pidUriResults.IsAnyResultEqualToIdentifierAndHasDifferentType(resourceId, resourceType))
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// Checks if the base uri in format of an uri is unique with one exception:
        /// 
        /// The base uri may only be the same across different versions of an entry.
        /// </summary>
        /// <param name="baseUriResults">All found entries with the same identifier</param>
        /// <param name="resource">Id of the entity to be checked</param>
        /// <param name="resourceId">Resource id of the main entry (parent entry id)</param>
        /// <param name="pidUri">Pid uri of resource to be checked</param>
        /// <param name="previousVersion">Pid uri of the previous version of the resource to be checked</param>
        private bool CheckBaseUriIsDuplicate(IList<DuplicateResult> baseUriResults, Entity resource, string resourceId, string pidUri, string previousVersion)
        {
            string resourceType = resource.Properties.GetValueOrNull(Graph.Metadata.Constants.RDF.Type, true);

            if (baseUriResults.IsNullOrEmpty())
            {
                return false;
            } 
            else if (baseUriResults.IsAnyDraftAndPublishedNull() || baseUriResults.IsAnyWithDifferentIdentifierType(Graph.Metadata.Constants.Resource.BaseUri))
            {
                return true;
            }
            else if (string.IsNullOrWhiteSpace(resourceId) && string.IsNullOrWhiteSpace(previousVersion))
            {
                return true;
            }

            var resourceVersions = GetVersions(pidUri, previousVersion);

            if (!baseUriResults.Any(result => resourceVersions.Any(version => result.Published == version.Id || result.Draft == version.Id)))
            {
                if (baseUriResults.IsAnyDraftAndPublishedNotEqualToIdentifier(resourceId))
                {
                    return true;
                }
                else if (baseUriResults.Any() && string.IsNullOrWhiteSpace(resourceId))
                {
                    return true;
                }
                else if (baseUriResults.IsAnyResultEqualToIdentifierAndHasDifferentType(resourceId, resourceType))
                {
                    return true;
                }
            }

            return false;
        }

        private IList<UrlCheckingCTO> GetIdentifierAndTargetUrls(Entity resource)
        {
            var identifiers = new List<UrlCheckingCTO>();

            foreach (var property in resource.Properties)
            {
                foreach (var prop in property.Value)
                {
                    if (property.Key == Graph.Metadata.Constants.Resource.DistributionEndpoints.HasNetworkAddress)
                    {
                        identifiers.Add(new UrlCheckingCTO(property.Key, resource.Id, prop));
                    }
                    else if (DynamicExtension.IsType<Entity>(prop, out Entity parsedProp))
                    {
                        if (property.Key == Graph.Metadata.Constants.EnterpriseCore.PidUri || property.Key == Graph.Metadata.Constants.Resource.BaseUri)
                        {
                            if (!string.IsNullOrWhiteSpace(parsedProp.Id))
                            {
                                identifiers.Add(new UrlCheckingCTO(property.Key, resource.Id, parsedProp.Id));
                            }
                        }
                        else
                        {
                            identifiers.AddRange(GetIdentifierAndTargetUrls(parsedProp));
                        }
                    }
                }
            }

            return identifiers;
        }

        private IList<DuplicateResult> GetDuplicateResultsByIdentifierType(string identifierType, Entity resource, out string identifier)
        {
            identifier = string.Empty;

            if (resource.Properties.ContainsKey(identifierType))
            {
                Entity duplicateRequestEntity = resource.Properties.GetValueOrNull(identifierType, true);

                if (duplicateRequestEntity != null && !string.IsNullOrWhiteSpace(duplicateRequestEntity.Id))
                {
                    identifier = duplicateRequestEntity.Id;
                    return _identifierService.GetPidUriIdentifierOccurrences(duplicateRequestEntity.Id);
                }
            }

            return new List<DuplicateResult>();
        }

        private IList<DuplicateResult> GetDuplicateResultsByTargetUri(Entity resource, out string targetUri)
        {
            targetUri = string.Empty;

            if (resource.Properties.ContainsKey(Graph.Metadata.Constants.Resource.DistributionEndpoints.HasNetworkAddress))
            {
                string duplicateRequestTargetUri = resource.Properties.GetValueOrNull(Graph.Metadata.Constants.Resource.DistributionEndpoints.HasNetworkAddress, true);

                if (!string.IsNullOrWhiteSpace(duplicateRequestTargetUri))
                {
                    targetUri = duplicateRequestTargetUri;
                    return CheckTargetUriDuplicate(duplicateRequestTargetUri);
                }
            }

            return new List<DuplicateResult>();
        }

        private IList<DuplicateResult> CheckTargetUriDuplicate(string duplicateRequestTargetUri)
        {
            var leafResourceTypes = _metadataService.GetInstantiableEntityTypes(Graph.Metadata.Constants.Resource.Type.FirstResouceType);
            return _resourceRepository.CheckTargetUriDuplicate(duplicateRequestTargetUri, leafResourceTypes);
        }

        private IList<VersionOverviewCTO> GetVersions(string pidUriString, string previousVersionString)
        {
            if (Uri.TryCreate(previousVersionString, UriKind.Absolute, out var previousVersion))
            {
                return _resourceRepository.GetAllVersionsOfResourceByPidUri(previousVersion);
            }

            if (Uri.TryCreate(pidUriString, UriKind.Absolute, out var pidUri))
            {
                return _resourceRepository.GetAllVersionsOfResourceByPidUri(pidUri);
            }

            return new List<VersionOverviewCTO>();
        }

        #endregion
    }
}
