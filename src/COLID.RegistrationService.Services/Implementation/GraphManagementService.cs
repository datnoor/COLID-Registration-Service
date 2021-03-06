﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Mime;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using COLID.Common.Extensions;
using COLID.Common.Utilities;
using COLID.Exception.Models;
using COLID.Exception.Models.Business;
using COLID.Graph.Metadata.DataModels.MetadataGraphConfiguration;
using COLID.Graph.Metadata.Services;
using COLID.Graph.Triplestore.Exceptions;
using COLID.Graph.TripleStore.AWS;
using COLID.Graph.TripleStore.Repositories;
using COLID.RegistrationService.Common.DataModel.Graph;
using COLID.RegistrationService.Repositories.Interface;
using COLID.RegistrationService.Services.Interface;
using COLID.StatisticsLog.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualBasic;

namespace COLID.RegistrationService.Services.Implementation
{
    public class GraphManagementService : IGraphManagementService
    {
        private readonly IGraphManagementRepository _graphManagementRepo;
        private readonly IGraphRepository _graphRepo;
        private readonly IMetadataGraphConfigurationService _graphConfigurationService;
        private readonly IAuditTrailLogService _auditTrailLogService;
        private readonly IAmazonS3Service _awsS3Service;
        private readonly INeptuneLoaderConnector _neptuneLoader;
        private readonly Regex _regex = new Regex(@"[\W\/]+");

        public GraphManagementService(IGraphManagementRepository graphManagementRepository, IGraphRepository graphRepository, 
            IMetadataGraphConfigurationService graphConfiguration, IAuditTrailLogService auditTrailLogService, IAmazonS3Service awsS3Service, INeptuneLoaderConnector neptuneLoader)
        {
            _graphManagementRepo = graphManagementRepository;
            _graphConfigurationService = graphConfiguration;
            _auditTrailLogService = auditTrailLogService;
            _graphRepo = graphRepository;
            _awsS3Service = awsS3Service;
            _neptuneLoader = neptuneLoader;
        }

        public IList<GraphDto> GetGraphs()
        {
            var graphNames = _graphManagementRepo.GetGraphs();
            var graphConfigs = _graphConfigurationService.GetConfigurationOverview();

            var currentGraphConfig = graphConfigs.FirstOrDefault();
            var historicGraphConfigs = graphConfigs.Where(t => t != currentGraphConfig);

            var graphs = graphNames.Select(graph =>
            {
                var graphUri = new Uri(graph);

                if (IsActiveGraph(graph, currentGraphConfig))
                {
                    return new GraphDto(graphUri, Common.Enums.Graph.GraphStatus.Active, currentGraphConfig.StartDateTime);
                }

                if (historicGraphConfigs.TryGetFirstOrDefault(c => c.Graphs.Contains(graph), out var historicGraphConfig))
                {
                    return new GraphDto(graphUri, Common.Enums.Graph.GraphStatus.Historic, historicGraphConfig.StartDateTime);
                }

                return new GraphDto(graphUri, Common.Enums.Graph.GraphStatus.Unreferenced, string.Empty);
            }).ToList();

            return graphs;
        }

        public void DeleteGraph(Uri graph)
        {
            if (graph == null || !graph.IsAbsoluteUri)
            {
                throw new ArgumentException(Common.Constants.Messages.Graph.InvalidFormat, nameof(graph));
            }

            var graphs = GetGraphs();
            var graphExists = graphs.TryGetFirstOrDefault(g => g.Name == graph, out var graphDto);

            if (!graphExists)
            {
                throw new GraphNotFoundException(Common.Constants.Messages.Graph.NotExists, graph);
            }

            if (graphDto.Status != Common.Enums.Graph.GraphStatus.Unreferenced)
            {
                throw new ReferenceException(Common.Constants.Messages.Graph.Referenced, graph.OriginalString);
            }

            _graphManagementRepo.DeleteGraph(graph);
            _auditTrailLogService.AuditTrail($"Graph in database with uri \"{graph}\" deleted.");
        }

        private bool IsActiveGraph(string graph, MetadataGraphConfigurationOverviewDTO currentGraphConfig)
        {
            var isInCurrentConfig = null != currentGraphConfig && currentGraphConfig.Graphs.Contains(graph);
            var isMetadataConfigGraph = graph == COLID.Graph.Metadata.Constants.MetadataGraphConfiguration.Type;

            return isInCurrentConfig || isMetadataConfigGraph;
        }

        public async Task<NeptuneLoaderResponse> ImportGraph(IFormFile turtleFile, Uri graphName, bool overwriteExisting = false)
        {
            Guard.IsValidUri(graphName);
            CheckFileTypeForTtl(turtleFile);

            var s3Key = await _awsS3Service.UploadFile(turtleFile);

            var graphExists = _graphRepo.CheckIfNamedGraphExists(graphName);
            if (graphExists && !overwriteExisting)
            {
                throw new GraphAlreadyExistsException(graphName);
            }
            
            var loaderResponse = await _neptuneLoader.LoadGraph(s3Key, graphName);
            return loaderResponse;
        }

        private static void CheckFileTypeForTtl(IFormFile turtleFile)
        {
            if (Path.GetExtension(turtleFile.FileName) != ".ttl" || !MediaTypeNames.Application.Octet.Equals(turtleFile.ContentType))
            {
                throw new BusinessException("The given file/content type is not valid, only .ttl-files are allowed.");
            }
        }

        public async Task<NeptuneLoaderStatusResponse> GetGraphImportStatus(Guid loadId)
        {
            var status = await _neptuneLoader.GetStatus(loadId);
            return status;
        }

    }
}
