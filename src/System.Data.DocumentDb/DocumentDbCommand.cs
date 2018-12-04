// Copyright (c) HQ.IO. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System.Collections;
using System.Collections.Generic;
using System.Data.Common;
using System.Diagnostics;
using System.Dynamic;
using System.Linq;
using System.Net;
using LiteGuard;
using Microsoft.Azure.Documents;
using Microsoft.Azure.Documents.Client;
using Microsoft.Azure.Documents.Linq;
using Newtonsoft.Json.Linq;

namespace System.Data.DocumentDb
{
    public sealed class DocumentDbCommand : DbCommand
    {
        private readonly DocumentDbParameterCollection _parameters;

        private DocumentDbConnection _connection;

        public DocumentDbCommand()
        {
            _parameters = new DocumentDbParameterCollection();
        }

        public DocumentDbCommand(DocumentDbConnection connection) : this()
        {
            _connection = connection;
        }

        protected override DbParameterCollection DbParameterCollection => _parameters;

        public override string CommandText { get; set; }

        protected override DbConnection DbConnection
        {
            get => _connection;
            set
            {
                Guard.AgainstNullArgument("value", value);

                if (value is DocumentDbConnection connection)
                    _connection = connection;
                else
                    throw new InvalidCastException("The connection passed was not a " + nameof(DocumentDbConnection) + ".");
            }
        }

        protected override DbParameter CreateDbParameter()
        {
            return new DocumentDbParameter();
        }

        public override object ExecuteScalar()
        {
            if (CommandText.Contains("COUNT"))
            {
                var options = new FeedOptions { MaxItemCount = 1 };
                var uri = UriFactory.CreateDocumentCollectionUri(_connection.Database, Collection);
                var query = this.ToQuerySpec();

                var result = _connection.Client.CreateDocumentQuery<long>(uri, query, options).AsDocumentQuery();
                var count = result.ExecuteNextAsync<long>().GetAwaiter().GetResult();
                return count.SingleOrDefault();
            }
            else
            {
                var resultSet = GetQueryResultSet();
                return resultSet?[0]?.ElementAt(0).Value;
            }
        }

        public override int ExecuteNonQuery()
        {
            if (CommandText.StartsWith(Constants.Insert))
                return InsertImpl();

            if (CommandText.StartsWith(Constants.Update))
                return UpdateImpl();

            if (CommandText.StartsWith(Constants.Delete))
                return DeleteImpl();

            var result = ExecuteScalar();
            return result is int value ? value : default;
        }

        private int UpdateImpl()
        {
            var options = new RequestOptions();
            var document = CommandToDocument(Constants.Update);

            const bool disableAutomaticIdGeneration = true;
            var uri = UriFactory.CreateDocumentCollectionUri(_connection.Database, Collection);
            var response = _connection.Client.UpsertDocumentAsync(uri, document, options, disableAutomaticIdGeneration).Result;
            return response.StatusCode == HttpStatusCode.OK ? 1 : 0;
        }

        private int InsertImpl()
        {
            var options = new RequestOptions();
            var document = CommandToDocument(Constants.Insert);

            var disableAutomaticIdGeneration = document.ContainsKey(Constants.IdKey);
            var uri = UriFactory.CreateDocumentCollectionUri(_connection.Database, Collection);
            var response = _connection.Client.CreateDocumentAsync(uri, document, options, disableAutomaticIdGeneration).Result;
            return response.StatusCode == HttpStatusCode.Created ? 1 : 0;
        }

        private int DeleteImpl()
        {
            var options = new RequestOptions();
            var document = CommandToDocument(Constants.Delete);

            var uri = UriFactory.CreateDocumentUri(_connection.Database, Collection, $"{document[Constants.IdKey]}");
            var response = _connection.Client.DeleteDocumentAsync(uri, options).Result;
            return response.StatusCode == HttpStatusCode.NoContent ? 1 : 0;
        }

        private Dictionary<string, object> CommandToDocument(string preamble)
        {
            var document = StartDocumentDefinition();

            switch (preamble)
            {
                case Constants.Insert:
                    {
                        var commandBase = CommandText.Substring(Constants.Insert.Length);
                        var collectionName = commandBase.Truncate(commandBase.IndexOf(" ", StringComparison.Ordinal));
                        var qualifier = collectionName + ".";

                        foreach (DocumentDbParameter parameter in _parameters)
                        {
                            var parameterName = parameter.ParameterName.Substring(qualifier.Length);
                            document.Add(parameterName, parameter.Value);

                            var parameterType = parameter.Value.GetType();
                            var isValidIdType = parameterType == typeof(string) || parameterType == typeof(Guid);
                            if (isValidIdType && parameterName == Id)
                                document.Add(Constants.IdKey, parameter.Value);

                            var isSequenceIdType = parameterType == typeof(long) || parameterType == typeof(int) || parameterType == typeof(short);
                            if (parameterName == Id && isSequenceIdType)
                            {
                                var options = new FeedOptions { MaxItemCount = 1 };
                                var uri = UriFactory.CreateDocumentCollectionUri(_connection.Database, Collection);

                                var sequence = new Dictionary<string, object>
                                {
                                    ["DocumentType"] = "Sequence",
                                    ["SequenceType"] = DocumentType
                                };

                                var query = new SqlQuerySpec("SELECT VALUE COUNT(1) FROM Sequence r WHERE r.DocumentType = @DocumentType AND r.SequenceType = @SequenceType");
                                query.Parameters.Add(new SqlParameter($"@{nameof(DocumentType)}", sequence["DocumentType"]));
                                query.Parameters.Add(new SqlParameter("@SequenceType", sequence["SequenceType"]));

                                var result = _connection.Client.CreateDocumentQuery<long>(uri, query, options).AsDocumentQuery();
                                var count = result.ExecuteNextAsync<long>().GetAwaiter().GetResult();
                                var nextId = count.SingleOrDefault() + 1;
                                sequence["Current"] = nextId;

                                query = new SqlQuerySpec("INSERT INTO Sequence (Sequence.DocumentType, Sequence.SequenceType, Sequence.Current) VALUES (@DocumentType, @SequenceType, @Current)");
                                query.Parameters.Add(new SqlParameter($"@{nameof(DocumentType)}", sequence["DocumentType"]));
                                query.Parameters.Add(new SqlParameter("@SequenceType", sequence["SequenceType"]));
                                query.Parameters.Add(new SqlParameter("@Current", sequence["Current"]));
                                
                                var sequenceOptions = new RequestOptions();
                                var disableAutomaticIdGeneration = document.ContainsKey(Constants.IdKey);
                                var response = _connection.Client.CreateDocumentAsync(uri, sequence, sequenceOptions, disableAutomaticIdGeneration).Result;
                                Debug.Assert(response.Resource.GetPropertyValue<long>(Id) == nextId);

                                document[Id] = nextId;
                                _connection.LastSequence = nextId;
                            }
                        }
                        break;
                    }
                default:
                    {
                        foreach (DocumentDbParameter parameter in _parameters)
                        {
                            document.Add(parameter.ParameterName, parameter.Value);

                            var parameterName = parameter.ParameterName;

                            var parameterType = parameter.Value.GetType();
                            var isValidIdType = parameterType == typeof(string) || parameterType == typeof(Guid);
                            if (isValidIdType && parameterName == Id)
                                document.Add(Constants.IdKey, parameter.Value);
                        }
                        break;
                    }
            }

            return document;
        }

        private Dictionary<string, object> StartDocumentDefinition()
        {
            var document = new Dictionary<string, object>();

            if (UseTypeDiscrimination)
                document.Add(nameof(DocumentType), DocumentType);

            return document;
        }

        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behaviour)
        {
            Debug.Assert(Type != null, nameof(Type) + " != null");
            IResultSet<ExpandoObject> resultSet = GetQueryResultSet();
            return new DocumentDbDataReader<ExpandoObject>(resultSet, Type);
        }

        private QueryResultSet GetQueryResultSet()
        {
            var options = new FeedOptions();

            var uri = UriFactory.CreateDocumentCollectionUri(_connection.Database, Collection);

            var query = this.ToQuerySpec();

            if (query.Parameters.Any(x => x.Name == "@Page"))
            {
                var selectClause = CommandText.Substring(CommandText.IndexOf(":::", StringComparison.Ordinal) + 3);

                CommandText = CommandText.Replace(selectClause, "r.id").Replace(":::r.id", string.Empty)
                    .Replace("SELECT", "SELECT VALUE ");

                if (UseTypeDiscrimination)
                {
                    var clause = CommandText.Contains("WHERE") ? "AND" : "WHERE";
                    CommandText += $" {clause} r.DocumentType = @DocumentType";
                }

                query = this.ToQuerySpec();
                MaybeTypeDiscriminate(query);

                var page = (int) query.Parameters.Single(x => x.Name == "@Page").Value;
                var perPage = (int) query.Parameters.Single(x => x.Name == "@PerPage").Value;
                options.MaxItemCount = page * perPage;
                
                var ids = new List<string>();
                var projection = _connection.Client.CreateDocumentQuery<List<string>>(uri, query, options).
                    AsDocumentQuery();
                while (projection.HasMoreResults)
                {
                    var next = projection.ExecuteNextAsync().GetAwaiter().GetResult();
                    if (next is IEnumerable)
                    {
                        foreach (var id in next)
                        {
                            if (id is JValue jv)
                                ids.Add(jv.Value as string);
                        }
                    }
                    else
                    {
                        if (next.SingleOrDefault() is JValue jv)
                            ids.Add(jv.Value as string);
                    }
                }

                {
                    var pageIds = ids.Skip(perPage * (page - 1));
                    var clause = CommandText.Contains("WHERE") ? "AND" : "WHERE";

                    CommandText = CommandText.Replace("r.id", selectClause).Replace("SELECT VALUE", "SELECT");
                    CommandText += $" {clause} r.id IN ('{string.Join("', '", pageIds)}')";

                    query = this.ToQuerySpec();
                    MaybeTypeDiscriminate(query);
                    var result = _connection.Client.CreateDocumentQuery<ExpandoObject>(uri, query, options);
                    var resultSet = new QueryResultSet();
                    resultSet.AddRange(result);
                    return resultSet;
                }
            }
            else
            {
                MaybeTypeDiscriminate(query);
                var result = _connection.Client.CreateDocumentQuery<ExpandoObject>(uri, query, options);
                var resultSet = new QueryResultSet();
                resultSet.AddRange(result);
                return resultSet;
            }
        }

        public void MaybeTypeDiscriminate(SqlQuerySpec query)
        {
            if (UseTypeDiscrimination)
                query.Parameters.Add(new SqlParameter($"@{nameof(DocumentType)}", DocumentType));
        }

        #region Custom Properties

        public Type Type { get; set; }
        public string Id { get; set; }
        public string Collection { get; set; }

        private bool UseTypeDiscrimination => Type != null && Collection != DocumentType;

        private string DocumentType
        {
            get
            {
                if (Type == null)
                    return null;
                var name = Type.Name;
                var index = name.IndexOf('`');
                return index == -1 ? name : name.Substring(0, index);
            }
        }

        #endregion

        #region Deactivated

        public override CommandType CommandType
        {
            get => CommandType.Text;
            set { }
        }

        public override bool DesignTimeVisible
        {
            get => false;
            set { }
        }

        protected override DbTransaction DbTransaction
        {
            get => null;
            set { }
        }

        public override int CommandTimeout
        {
            get => 0;
            set { }
        }

        public override UpdateRowSource UpdatedRowSource
        {
            get => UpdateRowSource.None;
            set { }
        }

        public override void Prepare()
        {
        }

        public override void Cancel()
        {
        }

        #endregion
    }
}
