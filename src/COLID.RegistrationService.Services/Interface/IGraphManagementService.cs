﻿using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using COLID.Graph.TripleStore.AWS;
using COLID.Graph.Triplestore.Exceptions;
using COLID.RegistrationService.Common.DataModel.Graph;
using Microsoft.AspNetCore.Http;

namespace COLID.RegistrationService.Services.Interface
{
    public interface IGraphManagementService
    {
        /// <summary>
        /// Returns a list of all named graphs stored in the database.
        ///
        /// The following stages exist:
        /// 1. Graph is in current config -> active
        /// 2. Graph is same as metadataconfig type -> active
        /// 3. Graph is in historized metadataconfig -> historic
        /// 4. Graph was used in the past or is new -> archivied
        /// </summary>
        /// <returns>List of graphs</returns>
        public IList<GraphDto> GetGraphs();
        
        /// <summary>
        /// Deletes a graph unless it is used by the system.
        /// </summary>
        /// <param name="graph">Graph name to be deleted</param>
        public void DeleteGraph(Uri graph);

        /// <summary>
        /// Import a graph with the given name into AWS Neptune. The ttl file will be uploaded to S3 first in order
        /// to import it properly. If no graph name as an uri was given, it will be generated from filename.
        /// </summary>
        /// <param name="turtleFile">The file to import</param>
        /// <param name="graphName">The graph name use as an Uri (optional)</param>
        /// <param name="overwriteExisting">to prevent accidental overwriting</param>
        /// <exception cref="GraphAlreadyExistsException">In case that the graph exists and overwrite is false</exception>
        public Task<NeptuneLoaderResponse> ImportGraph(IFormFile turtleFile, Uri graphName, bool overwriteExisting);

        /// <summary>
        /// Get the import status for the given load id.
        /// </summary>
        /// <param name="loadId">the id to fetch the status for</param>
        public Task<NeptuneLoaderStatusResponse> GetGraphImportStatus(Guid loadId);
    }
}
