﻿using System;
using System.Collections.Generic;
using System.Linq;
using Raven.Abstractions.Data;
using Raven.Server.Documents.Indexes;
using Raven.Server.Documents.Indexes.Auto;

namespace Raven.Server.Documents.Queries.Dynamic
{
    public class DynamicQueryMatchResult
    {
        public string IndexName { get; set; }
        public DynamicQueryMatchType MatchType { get; set; }

        public DynamicQueryMatchResult(string match, DynamicQueryMatchType matchType)
        {
            IndexName = match;
            MatchType = matchType;
        }

        public long LastMappedEtag { get; set; }

        public long NumberOfMappedFields { get; set;}
    }

    public enum DynamicQueryMatchType
    {
        Complete,
        Partial,
        Failure
    }
    
    public class DynamicQueryToIndexMatcher
    {
        private readonly IndexStore _indexStore;

        public DynamicQueryToIndexMatcher(IndexStore indexStore)
        {
            _indexStore = indexStore;
        }

        private delegate void ExplainDelegate(string index, Func<string> rejectionReasonGenerator);

        public class Explanation
        {
            public string Index { get; set; }
            public string Reason { get; set; }
        }

        // TODO arek - definitely too big method
        public DynamicQueryMatchResult Match(DynamicQueryMapping query, List<Explanation> explanations = null)
        {
            if (query.MapFields.Length == 0 && // we optimize for empty queries to use Raven/DocumentsByEntityName
                (query.SortDescriptors.Length == 0) // && // and no sorting was requested
                )
                //_indexStore.Contains(Constants.DocumentsByEntityNameIndex)) // and Raven/DocumentsByEntityName exists
            {
                // TODO arek
                throw new NotImplementedException("We don't support empty dynamic qeries for now");
                //if (string.IsNullOrEmpty(query.ForCollection) == false)
                //    indexQuery.Query = "Tag:" + entityName;
                //return new DynamicQueryMatchResult(Constants.DocumentsByEntityNameIndex, DynamicQueryMatchType.Complete);
                //}
            }

            ExplainDelegate explain = (index, rejectionReason) => { };
            if (explanations != null)
            {
                explain = (index, rejectionReason) => explanations.Add(new Explanation
                {
                    Index = index,
                    Reason = rejectionReason()
                });
            }
            
            var autoIndexes = _indexStore.GetAutoIndexDefinitionsForCollection(query.ForCollection); // let us work with AutoIndexes only for now

            if (autoIndexes.Count == 0)
                return new DynamicQueryMatchResult(string.Empty, DynamicQueryMatchType.Failure);

            var results = autoIndexes.Select(definition => ConsiderUsageOfAutoIndex(query, definition, explain))
            .Where(result => result.MatchType != DynamicQueryMatchType.Failure)
                    .GroupBy(x => x.MatchType)
                    .ToDictionary(x => x.Key, x => x.ToArray());

            DynamicQueryMatchResult[] matchResults;
            if (results.TryGetValue(DynamicQueryMatchType.Complete, out matchResults) && matchResults.Length > 0)
            {
                var prioritizedResults = matchResults
                    .OrderByDescending(x => x.LastMappedEtag)
                    .ThenByDescending(x => x.NumberOfMappedFields)
                    .ToArray();

                for (var i = 1; i < prioritizedResults.Length; i++)
                {
                    explain(prioritizedResults[i].IndexName,
                            () => "Wasn't the widest / most unstable index matching this query");
                }

                return prioritizedResults[0];
            }

            if (results.TryGetValue(DynamicQueryMatchType.Partial, out matchResults) && matchResults.Length > 0)
            {
                return matchResults.OrderByDescending(x => x.NumberOfMappedFields).First();
            }

            return new DynamicQueryMatchResult("", DynamicQueryMatchType.Failure);
        }

        private DynamicQueryMatchResult ConsiderUsageOfAutoIndex(DynamicQueryMapping query, AutoIndexDefinition definition, ExplainDelegate explain)
        {
            var collection = query.ForCollection;
            var indexName = definition.Name;

            if (definition.Collections.Contains(collection, StringComparer.OrdinalIgnoreCase) == false)
            {
                if (definition.Collections.Length == 0)
                    explain(indexName, () => "Query is specific for collection, but the index searches across all of them, may result in a different type being returned.");
                else
                    explain(indexName, () => $"Index does not apply to collection '{collection}'");

                return new DynamicQueryMatchResult(indexName, DynamicQueryMatchType.Failure);
            }
            else
            {
                if (definition.Collections.Length > 1) // we only allow indexes with a single entity name
                {
                    explain(indexName, () => "Index contains more than a single entity name, may result in a different type being returned.");
                    return new DynamicQueryMatchResult(indexName, DynamicQueryMatchType.Failure);
                }
            }

            var index = _indexStore.GetIndex(definition.Name);

            IndexingPriority priority = IndexingPriority.None; // TODO arek: use index.Priority

            if (priority == IndexingPriority.Error ||
                     priority == IndexingPriority.Disabled) //||
                                                            // TODO: arekisInvalidIndex)
            {
                explain(indexName, () => string.Format("Cannot do dynamic queries on disabled index or index with errors (index name = {0})", indexName));
                return new DynamicQueryMatchResult(indexName, DynamicQueryMatchType.Failure);
            }

            var currentBestState = DynamicQueryMatchType.Complete;

            if (query.MapFields.All(x => definition.ContainsField(x.From)) == false) // TODO arek: x.From
            {
                explain(indexName, () =>
                {
                    var missingFields = query.MapFields.Where(x => definition.ContainsField(x.From) == false); //TODO are: x.From
                    return $"The following fields are missing: {string.Join(", ", missingFields)}";
                });

                currentBestState = DynamicQueryMatchType.Partial;
            }

            //TODO arek: ignore sorting and highlighting for now
            //if (indexQuery.HighlightedFields != null && indexQuery.HighlightedFields.Length > 0)
            //{
            //    var nonHighlightableFields = indexQuery
            //        .HighlightedFields
            //        .Where(x =>
            //                !indexDefinition.Stores.ContainsKey(x.Field) ||
            //                indexDefinition.Stores[x.Field] != FieldStorage.Yes ||
            //                !indexDefinition.Indexes.ContainsKey(x.Field) ||
            //                indexDefinition.Indexes[x.Field] != FieldIndexing.Analyzed ||
            //                !indexDefinition.TermVectors.ContainsKey(x.Field) ||
            //                indexDefinition.TermVectors[x.Field] != FieldTermVector.WithPositionsAndOffsets)
            //        .Select(x => x.Field)
            //        .ToArray();

            //    if (nonHighlightableFields.Any())
            //    {
            //        explain(indexName,
            //            () => "The following fields could not be highlighted because they are not stored, analyzed and using term vectors with positions and offsets: " +
            //                  string.Join(", ", nonHighlightableFields));
            //        return new DynamicQueryOptimizerResult(indexName, DynamicQueryMatchType.Failure);
            //    }
            //}

            //if (indexQuery.SortedFields != null && indexQuery.SortedFields.Length > 0)
            //{
            //    var sortInfo = DynamicQueryMapping.GetSortInfo(s => { }, indexQuery);

            //    foreach (var sortedField in indexQuery.SortedFields) // with matching sort options
            //    {
            //        var sortField = sortedField.Field;
            //        if (sortField.StartsWith(Constants.AlphaNumericFieldName) ||
            //            sortField.StartsWith(Constants.RandomFieldName) ||
            //            sortField.StartsWith(Constants.CustomSortFieldName))
            //        {
            //            sortField = SortFieldHelper.CustomField(sortField).Name;
            //        }

            //        var normalizedFieldName = DynamicQueryMapping.ReplaceInvalidCharactersForFields(sortField);

            //        if (normalizedFieldName.EndsWith("_Range"))
            //            normalizedFieldName = normalizedFieldName.Substring(0, normalizedFieldName.Length - "_Range".Length);

            //        // if the field is not in the output, then we can't sort on it. 
            //        if (abstractViewGenerator.ContainsField(normalizedFieldName) == false)
            //        {
            //            explain(indexName,
            //                    () =>
            //                    "Rejected because index does not contains field '" + normalizedFieldName + "' which we need to sort on");
            //            currentBestState = DynamicQueryMatchType.Partial;
            //            continue;
            //        }

            //        var dynamicSortInfo = sortInfo.FirstOrDefault(x => x.Field == normalizedFieldName);

            //        if (dynamicSortInfo == null)// no sort order specified, we don't care, probably
            //            continue;

            //        SortOptions value;
            //        if (indexDefinition.SortOptions.TryGetValue(normalizedFieldName, out value) == false)
            //        {
            //            switch (dynamicSortInfo.FieldType)// if we can't find the value, we check if we asked for the default sorting
            //            {
            //                case SortOptions.String:
            //                case SortOptions.None:
            //                    continue;
            //                default:
            //                    explain(indexName,
            //                            () => "The specified sort type is different than the default for field: " + normalizedFieldName);
            //                    return new DynamicQueryOptimizerResult(indexName, DynamicQueryMatchType.Failure);
            //            }
            //        }

            //        if (value != dynamicSortInfo.FieldType)
            //        {
            //            explain(indexName,
            //                    () =>
            //                    "The specified sort type (" + dynamicSortInfo.FieldType + ") is different than the one specified for field '" +
            //                    normalizedFieldName + "' (" + value + ")");
            //            return new DynamicQueryOptimizerResult(indexName, DynamicQueryMatchType.Failure);
            //        }
            //    }
            //}
            
            if (currentBestState != DynamicQueryMatchType.Complete) // TODO arek
                return new DynamicQueryMatchResult(indexName, DynamicQueryMatchType.Failure);

            if (currentBestState == DynamicQueryMatchType.Complete && (priority == IndexingPriority.Idle || priority == IndexingPriority.Abandoned))
            {
                currentBestState = DynamicQueryMatchType.Partial;
                explain(indexName, () => String.Format("The index (name = {0}) is disabled or abandoned. The preference is for active indexes - making a partial match", indexName));
            }

            return new DynamicQueryMatchResult(indexName, currentBestState)
            {
                LastMappedEtag = index.GetLastMappedEtag(),
                NumberOfMappedFields = definition.MapFields.Count() // TODO arek
            };
        }
    }
}