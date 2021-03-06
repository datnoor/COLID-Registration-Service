﻿using System.Collections.Generic;
using System.Linq;
using AutoMapper;
using COLID.Cache.Services;
using COLID.Exception.Models.Business;
using COLID.Graph.Metadata.Services;
using COLID.Graph.TripleStore.DataModels.Base;
using COLID.Graph.TripleStore.DataModels.Taxonomies;
using COLID.Graph.TripleStore.Services;
using COLID.RegistrationService.Repositories.Interface;
using COLID.RegistrationService.Services.Interface;
using Microsoft.Extensions.Logging;

namespace COLID.RegistrationService.Services.Implementation
{
    public class TaxonomyService : BaseEntityService<Taxonomy, TaxonomyRequestDTO, TaxonomyResultDTO, BaseEntityResultCTO, ITaxonomyRepository>, ITaxonomyService
    {
        private readonly ITaxonomyRepository _taxonomyRepository;
        private readonly ICacheService _cacheService;

        public TaxonomyService(
            IMapper mapper,
            IMetadataService metadataService,
            IValidationService validationService,
            ITaxonomyRepository taxonomyRepository,
            ILogger<TaxonomyService> logger,
            ICacheService cacheService) : base(mapper, metadataService, validationService, taxonomyRepository, logger)
        {
            _taxonomyRepository = taxonomyRepository;
            _cacheService = cacheService;
        }

        public override TaxonomyResultDTO GetEntity(string id)
        {
            var taxonomy = _cacheService.GetOrAdd($"id:{id}", () =>
            {
                var taxonomies = _taxonomyRepository.GetTaxonomiesByIdentifier(id);
                var transformed = TransformTaxonomyListToHierarchy(taxonomies, id).FirstOrDefault();

                if (transformed == null)
                {
                    throw new EntityNotFoundException(Common.Constants.Messages.Taxonomy.NotFound, id);
                }

                return transformed;
            });

            return taxonomy;
        }

        public IList<TaxonomyResultDTO> GetTaxonomies(string taxonomyType)
        {
            var taxonomies = _cacheService.GetOrAdd($"type:{taxonomyType}", () =>
            {
                var taxonomyList = _taxonomyRepository.GetTaxonomies(taxonomyType);
                return TransformTaxonomyListToHierarchy(taxonomyList);
            });
            return taxonomies;
        }

        public IList<TaxonomyResultDTO> GetTaxonomiesAsPlainList(string taxonomyType)
        {
            var plainTaxonomies = _cacheService.GetOrAdd($"list:type:{taxonomyType}", () =>
            {
                var taxonomies = _taxonomyRepository.GetTaxonomies(taxonomyType);
                return CreateHierarchicalStructureFromTaxonomyList(taxonomies);
            });

            return plainTaxonomies;
        }

        /// <summary>
        /// Transforms a given taxonomy list to a hierarchical structure and returns the top parents and their children.
        /// If a TopTaxonomyIdentifier is given, this taxonomy and its children are returned.
        /// </summary>
        /// <param name="taxonomyList">Plain list of all taxonomies without any hierarchy</param>
        /// <param name="topTaxonomyIdentifier">Identifier of the taxonomy item, that should get returned</param>
        /// <returns>Returns the matching taxonomy and its children, if topTaxonomyIdentifier is set. Otherwise, returns whole taxonomy structure.</returns>
        private IList<TaxonomyResultDTO> TransformTaxonomyListToHierarchy(IEnumerable<Taxonomy> taxonomyList, string topTaxonomyIdentifier = null)
        {
            var taxonomyHierarchy = CreateHierarchicalStructureFromTaxonomyList(taxonomyList);

            if (!string.IsNullOrWhiteSpace(topTaxonomyIdentifier))
            {
                return taxonomyHierarchy.Where(t => t.Id == topTaxonomyIdentifier).ToList();
            }

            return taxonomyHierarchy.Where(t => !t.HasParent).ToList();
        }

        /// <summary>
        /// Creates a hierarchical structure of the given plain list of taxonomies by adding object references of children to their parent taxonomies.
        /// All children, which have been added to the parents, remain in the main list.
        /// </summary>
        /// <param name="taxonomyList">Plain list of all taxonomies without any hierarchy</param>
        /// <returns>A list of all taxonomies with a hierarchical structure of child taxonomies</returns>
        private IList<TaxonomyResultDTO> CreateHierarchicalStructureFromTaxonomyList(IEnumerable<Taxonomy> taxonomyList)
        {
            var taxonomies = taxonomyList.ToDictionary(t => t.Id, t => _mapper.Map<TaxonomyResultDTO>(t));

            foreach (var taxonomy in taxonomies)
            {
                var child = taxonomy.Value;

                if (child.Properties.TryGetValue(Graph.Metadata.Constants.SKOS.Broader, out var parents))
                {
                    foreach (var parent in parents)
                    {
                        if (taxonomies.TryGetValue(parent, out TaxonomyResultDTO parentTaxonomy))
                        {
                            parentTaxonomy.Children.Add(child);
                        }
                    }
                }
            }

            return taxonomies.Values.ToList();
        }
    }
}
