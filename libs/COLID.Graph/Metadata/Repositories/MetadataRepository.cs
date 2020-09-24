﻿using System;
using System.Collections.Generic;
using System.Linq;
using COLID.Common.Extensions;
using COLID.Exception.Models.Business;
using VDS.RDF;
using VDS.RDF.Query;
using Microsoft.Extensions.Logging;
using COLID.Graph.TripleStore.Repositories;
using COLID.Graph.Metadata.DataModels.Metadata;
using COLID.Graph.Metadata.DataModels.MetadataGraphConfiguration;
using COLID.Graph.TripleStore.Extensions;
using COLID.Graph.TripleStore.DataModels.Base;
using COLID.Graph.TripleStore.DataModels.Sparql;

namespace COLID.Graph.Metadata.Repositories
{
    internal class MetadataRepository : IMetadataRepository
    {
        private readonly ILogger<MetadataRepository> _logger;
        private readonly ITripleStoreRepository _tripleStoreRepository;
        private readonly IMetadataGraphConfigurationRepository _metadataGraphConfigurationRepository;

        public MetadataRepository(
            ITripleStoreRepository tripleStoreRepository,
            ILogger<MetadataRepository> logger,
            IMetadataGraphConfigurationRepository metadataGraphConfigurationRepository)
        {
            _tripleStoreRepository = tripleStoreRepository;
            _logger = logger;
            _metadataGraphConfigurationRepository = metadataGraphConfigurationRepository;
        }

        public IList<MetadataProperty> GetMetadataForEntityTypeInConfig(string entityType, string configIdentifier = null)
        {
            if (!entityType.IsValidBaseUri())
            {
                throw new InvalidFormatException(Constants.Messages.Identifier.IncorrectIdentifierFormat, entityType);
            }

            MetadataGraphConfiguration usedConfig;
            if (string.IsNullOrWhiteSpace(configIdentifier))
            {
                _logger.LogInformation($"Got request for entity type {entityType} with no config");
                usedConfig = _metadataGraphConfigurationRepository.GetLatestConfiguration();
            }
            else
            {
                _logger.LogInformation($"Got request for entity type {entityType} with config {configIdentifier}");
                usedConfig = _metadataGraphConfigurationRepository.GetEntityById(configIdentifier);
            }

            SparqlParameterizedString queryString = new SparqlParameterizedString();

            queryString.CommandText = @"
                SELECT *
                @fromMetadataNamedGraph
                @fromMetdataShaclNamedGraph
                @fromEnterpriseCoreOntologyNamedGraph
                @fromConsumerGroupNamedGraph
                WHERE
                {
                    {
                        @entityType rdfs:subClassOf*  ?resourceType.
                        @entityType rdfs:label ?typeLabel.
                        FILTER(lang(?typeLabel) IN (@language , """"))
                        Bind(@entityType as ?type).
                        ?resourceType sh:property ?shaclConstraint.
                        ?shaclConstraint ?shaclProperty ?shaclValue.
                        OPTIONAL
                        {
                            ?shaclValue rdf:type ?dataType.
                        }
                        OPTIONAL {
  	                        ?shaclConstraint @editWidget @nestedObjectEditor.
                            ?shaclConstraint sh:class ?nested
                        }
                        OPTIONAL
                        {
                            ?shaclValue rdf:type sh:PropertyGroup.
                            Bind(?shaclValue as ?group).
                            ?shaclValue sh:order ?groupOrder.
                            ?shaclValue rdfs:label ?grouplabel.
                            ?shaclValue tosh:editGroupDescription ?editGroupDescription.
                            ?shaclValue tosh:viewGroupDescription ?viewGroupDescription
                        }
                        OPTIONAL
                        {
                            ?shaclValue sh:class ?class.
                        }
                    }
                    UNION
                    {
                        @entityType rdfs:subClassOf* ?resourceType.
                        ?extraProperty rdfs:domain ?resourceType.
                        ?extraProperty ?shaclProperty ?shaclValue
                    }
                }";

            queryString.SetPlainLiteral("fromMetadataNamedGraph", usedConfig.GetMetadataGraphs().JoinAsFromNamedGraphs());
            queryString.SetPlainLiteral("fromMetdataShaclNamedGraph", usedConfig.GetShaclGraphs().JoinAsFromNamedGraphs());
            queryString.SetPlainLiteral("fromEnterpriseCoreOntologyNamedGraph", usedConfig.GetEcoGraphs().JoinAsFromNamedGraphs());
            queryString.SetPlainLiteral("fromConsumerGroupNamedGraph", usedConfig.GetConsumerGroupGraphs().JoinAsFromNamedGraphs());

            queryString.SetUri("entityType", new Uri(entityType));
            queryString.SetLiteral("language", Constants.I18n.DefaultLanguage);

            queryString.SetUri("editWidget", new Uri(Constants.TopBraid.EditWidget));
            queryString.SetUri("nestedObjectEditor", new Uri(Constants.TopBraid.NestedObjectEditor));

            SparqlResultSet results = _tripleStoreRepository.QueryTripleStoreResultSet(queryString);

            var whereNotNullResults = results.Where(r => GetDataFromNode(r, "shaclConstraint")?.Value != null);
            var whereNullResults = results.Where(r => GetDataFromNode(r, "shaclConstraint").Value == null).ToList();
            var groupedResults = whereNotNullResults.GroupBy(res => GetDataFromNode(res, "shaclConstraint")?.Value);
            var dict = GroupedSparqResultToDictionary(groupedResults);

            foreach (var r in whereNullResults)
            {
                var key = GetDataFromNode(r, "extraProperty")?.Value;

                if (dict.ContainsKey(key))
                {
                    dict[key].Add(r);
                }
            }

            var metaDataPropertyDictionary = dict.ToDictionary(r => r.Key, r => CreateMetadataPropertyFromList(r.Key, r.Value, configIdentifier));

            // MainDistribution is a subproperty of distribution and gets no nested metadata
            if (metaDataPropertyDictionary.TryGetValue(Constants.Resource.MainDistribution, out var mainProperties) && metaDataPropertyDictionary.TryGetValue(Constants.Resource.Distribution, out var properties))
            {
                mainProperties.NestedMetadata = properties.NestedMetadata;
            }

            var metaDataPropertyList = metaDataPropertyDictionary.Values.ToList();

            // Remove all properties without label -> must have for property
            // metaDataPropertyList = metaDataPropertyList.Where(r => r.Properties.Any(t => t.Key == Shacl.Name)).ToList();

            return metaDataPropertyList;
        }

        public EntityTypeDto GetEntityTypes(string firstEntityType)
        {
            if (!firstEntityType.IsValidBaseUri())
            {
                throw new InvalidFormatException(Constants.Messages.Identifier.IncorrectIdentifierFormat, firstEntityType);
            }

            SparqlParameterizedString queryString = new SparqlParameterizedString();

            queryString.CommandText =
                @"SELECT DISTINCT * 
                      @fromMetadataNamedGraph
                      @fromMetadataShacledNamedGraph
                      @fromEnterpriseCoreOntologyNamedGraph
                        WHERE {
                          ?subject rdfs:subClassOf* @value .
                          ?subject ?predicate ?object.
                          BIND (exists { ?subClass rdfs:subClassOf ?subject } AS ?subClassExists )
                          FILTER(lang(str(?object)) IN (@language , """"))
                          } ORDER BY ?subject";

            queryString.SetPlainLiteral("fromMetadataNamedGraph", _metadataGraphConfigurationRepository.GetGraphs(Constants.MetadataGraphConfiguration.HasMetadataGraph).JoinAsFromNamedGraphs());
            queryString.SetPlainLiteral("fromMetadataShacledNamedGraph", _metadataGraphConfigurationRepository.GetGraphs(Constants.MetadataGraphConfiguration.HasShaclConstraintsGraph).JoinAsFromNamedGraphs());
            queryString.SetPlainLiteral("fromEnterpriseCoreOntologyNamedGraph", _metadataGraphConfigurationRepository.GetGraphs(Constants.MetadataGraphConfiguration.HasECOGraph).JoinAsFromNamedGraphs());

            queryString.SetLiteral("language", Constants.I18n.DefaultLanguage);
            queryString.SetUri("value", new Uri(firstEntityType));

            SparqlResultSet results = _tripleStoreRepository.QueryTripleStoreResultSet(queryString);

            if (results.IsEmpty)
            {
                return null;
            }

            var entities = TransformQueryResults<EntityTypeDto>(results);
            var resourceTypeLookup = entities.ToDictionary(t => t.Id, t => t);

            foreach (var link in resourceTypeLookup)
            {
                var entity = link.Value;

                if (entity.Properties.TryGetValue(Constants.RDFS.SubClassOf, out var parents))
                {
                    foreach (var parentId in parents)
                    {
                        if (resourceTypeLookup.TryGetValue(parentId, out EntityTypeDto parent))
                        {
                            parent.SubClasses.Add(entity);
                        }
                    }
                }
            }

            if (resourceTypeLookup.TryGetValue(firstEntityType, out var resourceTypeHierarchy))
            {
                return resourceTypeHierarchy;
            };

            return null;
        }

        public IList<string> GetParentEntityTypes(string firstEntityType)
        {
            if (!firstEntityType.IsValidBaseUri())
            {
                throw new InvalidFormatException(Constants.Messages.Identifier.IncorrectIdentifierFormat, firstEntityType);
            }

            SparqlParameterizedString queryString = new SparqlParameterizedString();

            queryString.CommandText =
                @"SELECT *
                  @fromMetadataNamedGraph
                  @fromMetdataShaclNamedGraph
                  @fromEnterpriseCoreOntologyNamedGraph
                  WHERE {
                      @value rdfs:subClassOf* ?type.
                  }";

            queryString.SetPlainLiteral("fromMetadataNamedGraph", _metadataGraphConfigurationRepository.GetGraphs(Constants.MetadataGraphConfiguration.HasMetadataGraph).JoinAsFromNamedGraphs());
            queryString.SetPlainLiteral("fromMetdataShaclNamedGraph", _metadataGraphConfigurationRepository.GetGraphs(Constants.MetadataGraphConfiguration.HasShaclConstraintsGraph).JoinAsFromNamedGraphs());
            queryString.SetPlainLiteral("fromEnterpriseCoreOntologyNamedGraph", _metadataGraphConfigurationRepository.GetGraphs(Constants.MetadataGraphConfiguration.HasECOGraph).JoinAsFromNamedGraphs());

            queryString.SetUri("value", new Uri(firstEntityType));

            SparqlResultSet results = _tripleStoreRepository.QueryTripleStoreResultSet(queryString);

            if (results.IsEmpty)
            {
                return null;
            }

            var resourceTypes = results.Select(result => result.GetNodeValuesFromSparqlResult("type").Value).ToList();

            return resourceTypes;
        }

        public IList<string> GetLeafEntityTypes(string firstEntityType)
        {
            if (!firstEntityType.IsValidBaseUri())
            {
                throw new InvalidFormatException(Constants.Messages.Identifier.IncorrectIdentifierFormat, firstEntityType);
            }

            SparqlParameterizedString queryString = new SparqlParameterizedString();

            queryString.CommandText =
                @"SELECT *
                  @fromMetadataNamedGraph
                  @fromMetdataShaclNamedGraph
                  @fromEnterpriseCoreOntologyNamedGraph
                  WHERE {
                      ?type rdfs:subClassOf* @value.
                      Filter not exists { ?subClassOf rdfs:subClassOf ?type}
                  }";

            queryString.SetPlainLiteral("fromMetadataNamedGraph", _metadataGraphConfigurationRepository.GetGraphs(Constants.MetadataGraphConfiguration.HasMetadataGraph).JoinAsFromNamedGraphs());
            queryString.SetPlainLiteral("fromMetdataShaclNamedGraph", _metadataGraphConfigurationRepository.GetGraphs(Constants.MetadataGraphConfiguration.HasShaclConstraintsGraph).JoinAsFromNamedGraphs());
            queryString.SetPlainLiteral("fromEnterpriseCoreOntologyNamedGraph", _metadataGraphConfigurationRepository.GetGraphs(Constants.MetadataGraphConfiguration.HasECOGraph).JoinAsFromNamedGraphs());

            queryString.SetUri("value", new Uri(firstEntityType));

            SparqlResultSet results = _tripleStoreRepository.QueryTripleStoreResultSet(queryString);

            if (results.IsEmpty)
            {
                return null;
            }

            var resourceTypes = results.Select(result => result.GetNodeValuesFromSparqlResult("type").Value).ToList();

            return resourceTypes;
        }

        public IList<string> GetInstantiableEntityTypes(string firstEntityType)
        {
            if (!firstEntityType.IsValidBaseUri())
            {
                throw new InvalidFormatException(Constants.Messages.Identifier.IncorrectIdentifierFormat, firstEntityType);
            }

            SparqlParameterizedString queryString = new SparqlParameterizedString
            {
                CommandText =
                @"SELECT DISTINCT *
                      @fromMetadataNamedGraph
                      @fromEnterpriseCoreOntologyNamedGraph
                        WHERE {
                          ?class rdfs:subClassOf* @firstEntityType .
                          ?class @dashAbstract false.
                          } ORDER BY ?class"
            };

            queryString.SetPlainLiteral("fromMetadataNamedGraph", _metadataGraphConfigurationRepository.GetGraphs(Constants.MetadataGraphConfiguration.HasMetadataGraph).JoinAsFromNamedGraphs());
            queryString.SetPlainLiteral("fromEnterpriseCoreOntologyNamedGraph", _metadataGraphConfigurationRepository.GetGraphs(Constants.MetadataGraphConfiguration.HasECOGraph).JoinAsFromNamedGraphs());
            queryString.SetUri("firstEntityType", new Uri(firstEntityType));
            queryString.SetUri("dashAbstract", new Uri(Constants.DASH.Abstract));

            SparqlResultSet results = _tripleStoreRepository.QueryTripleStoreResultSet(queryString);

            var types = results
                .Select(r => r.GetNodeValuesFromSparqlResult("class")?.Value)
                .Where(r => !string.IsNullOrWhiteSpace(r))
                .ToList();

            return types;
        }

        private MetadataProperty CreateMetadataPropertyFromList(string pidUri, List<SparqlResult> sparqlResults, string configIdentifier)
        {
            var metadataProperty = new MetadataProperty();

            metadataProperty.Properties.Add(Constants.EnterpriseCore.PidUri, pidUri);

            foreach (var res in sparqlResults)
            {
                var shaclProperty = GetDataFromNode(res, "shaclProperty").Value;
                var shaclValue = GetDataFromNode(res, "shaclValue").Value;

                metadataProperty.Properties.AddOrUpdate(shaclProperty, shaclValue);

                switch (shaclProperty)
                {
                    case Constants.TopBraid.EditWidget when shaclValue == Constants.TopBraid.NestedObjectEditor:
                        var nestedMetaDataType = GetDataFromNode(res, "nested").Value;
                        var nestedTypes = GetEntityTypes(nestedMetaDataType);
                        metadataProperty.NestedMetadata = nestedTypes.SubClasses.Select(r => new DataModels.Metadata.Metadata(r.Id, r.Label, r.Description, GetMetadataForEntityTypeInConfig(r.Id, configIdentifier))).ToList();
                        break;

                    case Constants.Shacl.Path when shaclValue == Constants.RDF.Type:
                        metadataProperty.Properties.AddOrUpdate(Constants.Shacl.Range, Constants.OWL.Class);
                        break;

                    case Constants.Shacl.Group:
                        var group = new MetadataPropertyGroup();
                        group.Key = GetDataFromNode(res, "group")?.Value;
                        group.Label = GetDataFromNode(res, "grouplabel")?.Value;
                        decimal.TryParse(GetDataFromNode(res, "groupOrder")?.Value, out decimal order);

                        group.Order = order;
                        group.EditDescription = GetDataFromNode(res, "editGroupDescription")?.Value;
                        group.ViewDescription = GetDataFromNode(res, "viewGroupDescription")?.Value;
                        metadataProperty.Properties.AddOrUpdate(Constants.Shacl.Group, group);

                        break;
                }
            };

            return metadataProperty;
        }

        private SparqlResponseProperty GetDataFromNode(SparqlResult sparqlResult, string key)
        {
            return sparqlResult.GetNodeValuesFromSparqlResult(key);
        }

        // TODO: Add cache -> IGraph serializable?
        public IGraph GetAllShaclAsGraph()
        {
            SparqlParameterizedString parameterizedString = new SparqlParameterizedString();
            parameterizedString.CommandText =
                @"
                  CONSTRUCT
                  @fromMetadataNamedGraph
                  @fromMetdataShaclNamedGraph
                  @fromEnterpriseCoreOntologyNamedGraph
                  WHERE {
                      ?s ?o ?p
                  }";

            parameterizedString.SetPlainLiteral("fromMetadataNamedGraph", _metadataGraphConfigurationRepository.GetGraphs(Constants.MetadataGraphConfiguration.HasMetadataGraph).JoinAsFromNamedGraphs());
            parameterizedString.SetPlainLiteral("fromMetdataShaclNamedGraph", _metadataGraphConfigurationRepository.GetGraphs(Constants.MetadataGraphConfiguration.HasShaclConstraintsGraph).JoinAsFromNamedGraphs());
            parameterizedString.SetPlainLiteral("fromEnterpriseCoreOntologyNamedGraph", _metadataGraphConfigurationRepository.GetGraphs(Constants.MetadataGraphConfiguration.HasECOGraph).JoinAsFromNamedGraphs());

            //Get raw data from TripleStore
            return _tripleStoreRepository.QueryTripleStoreGraphResult(parameterizedString);
        }

        private Dictionary<string, List<SparqlResult>> GroupedSparqResultToDictionary(IEnumerable<IGrouping<string, SparqlResult>> groupedResults)
        {
            var dict = new Dictionary<string, List<SparqlResult>>();

            foreach (var r in groupedResults)
            {
                var key = GetMetadataPropertyKeyFromSparqlResults(r.ToList());

                if (!dict.ContainsKey(key))
                {
                    dict.Add(key, r.ToList());
                }
                else
                {
                    _logger.LogInformation($"Duplicate of metadata property: {key}");
                }
            }

            return dict;
        }

        private string GetMetadataPropertyKeyFromSparqlResults(IList<SparqlResult> sparqlResults)
        {
            var sparqlResult = sparqlResults.FirstOrDefault(p => GetDataFromNode(p, "shaclProperty")?.Value == Constants.Shacl.Path);
            var key = GetDataFromNode(sparqlResult, "shaclValue")?.Value;

            return key;
        }

        public string GetEntityLabelById(string id)
        {
            if (!id.IsValidBaseUri())
            {
                throw new InvalidFormatException(Constants.Messages.Identifier.IncorrectIdentifierFormat, id);
            }

            var parameterizedString = new SparqlParameterizedString();
            parameterizedString.CommandText =
              @"SELECT ?label
                  @fromMetadataNamedGraph
                  WHERE {
                      @subject @predicate ?label
                  }";

            parameterizedString.SetPlainLiteral("fromMetadataNamedGraph", _metadataGraphConfigurationRepository.GetGraphs(Constants.MetadataGraphConfiguration.HasMetadataGraph).JoinAsFromNamedGraphs());
            parameterizedString.SetUri("subject", new Uri(id));
            parameterizedString.SetUri("predicate", new Uri(Constants.RDFS.Label));

            var results = _tripleStoreRepository.QueryTripleStoreResultSet(parameterizedString);

            return results.Any() ? results.FirstOrDefault().GetNodeValuesFromSparqlResult("label")?.Value : string.Empty;
        }

        /// <summary>
        /// IMPORTANT: It's neccesary to identify the colums with "id", "predicate" and "object" when you query the graph, so the fields can be transformed properly.
        /// </summary>
        private IList<T> TransformQueryResults<T>(SparqlResultSet results, string id = "") where T : Entity, new()
        {
            if (results.IsEmpty)
            {
                return new List<T>();
            }

            var groupedResults = results.GroupBy(result => result.GetNodeValuesFromSparqlResult("subject").Value);

            IList<T> foundEntities = groupedResults.Select(result =>
            {
                var subGroupedResults = result.GroupBy(res => res.GetNodeValuesFromSparqlResult("predicate").Value);
                var newEntity = new T
                {
                    Id = id == string.Empty ? result.Key : id,
                    Properties = subGroupedResults.ToDictionary(x => x.Key, x => x.Select(property => GetEntityPropertyFromSparqlResult(property)).ToList())
                };

                return newEntity;
            }).ToList();

            return foundEntities;
        }

        private dynamic GetEntityPropertyFromSparqlResult(SparqlResult res)
        {
            return res.GetNodeValuesFromSparqlResult("object").Value;
        }

    }
}
