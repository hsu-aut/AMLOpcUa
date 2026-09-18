
/* ========================================================================
 * Copyright (c) 2005-2021 The OPC Foundation, Inc. All rights reserved.
 *
 * OPC Foundation MIT License 1.00
 * 
 * Permission is hereby granted, free of charge, to any person
 * obtaining a copy of this software and associated documentation
 * files (the "Software"), to deal in the Software without
 * restriction, including without limitation the rights to use,
 * copy, modify, merge, publish, distribute, sublicense, and/or sell
 * copies of the Software, and to permit persons to whom the
 * Software is furnished to do so, subject to the following
 * conditions:
 * 
 * The above copyright notice and this permission notice shall be
 * included in all copies or substantial portions of the Software.
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
 * EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES
 * OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
 * NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT
 * HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY,
 * WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
 * FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR
 * OTHER DEALINGS IN THE SOFTWARE.
 *
 * The complete license agreement can be found here:
 * http://opcfoundation.org/License/MIT/1.00/
 * ======================================================================*/

using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Xml;
using System.Xml.Serialization;
using System.Reflection;
using System.Text;
using Opc.Ua;
using MarkdownProcessor.NodeSet;

namespace MarkdownProcessor
{
    public class ModelManager
    {
        public ModelInfo DefaultModel { get; private set; }
        public NamespaceTable NamespaceUris { get; private set; }
        public StringTable ServerUris { get; private set; }
        public Dictionary<NodeId, UANode> Nodes { get; private set; }
        public Dictionary<string, ModelInfo> Models { get; private set; }
        public List<ModelInfo> ModelNamespaceIndexes { get; private set; }

        public event EventHandler<ModelRequiredEventArgs> ModelRequired;

        private Dictionary<NodeId, List<ReferenceInfo>> References;

        private class Context
        {
            public UANodeSet NodeSet;
            public List<ushort> NamespaceMappings;
            public List<uint> ServerUriMappings;
            public Dictionary<string, NodeId> Aliases;
        }

        public ModelManager()
        {
            NamespaceUris = new NamespaceTable();
            ServerUris = new StringTable();
            Nodes = new Dictionary<NodeId, UANode>();
            Models = new Dictionary<string, ModelInfo>();
            References = new Dictionary<NodeId, List<ReferenceInfo>>();
            ModelNamespaceIndexes = new List<ModelInfo>();
        }

        public void LoadModelIfNotLoaded(string modelUri, string filePath, string repositoryPath, string baseWebUrl)
        {
            if (!File.Exists(filePath))
            {
                return;
            }

            if (Models.ContainsKey(modelUri))
            {
                return;
            }

            LoadModel(filePath, filePath, filePath);
        }

        public string LoadModel(string filePath, string repositoryPath, string baseWebUrl)
        {
            // load the model from disk.
            var nodeset = Read(filePath);
            string namespaceUri = null;

            // recursively load all required models.
            if (nodeset.Models != null)
            {
                foreach (var ii in nodeset.Models)
                {
                    if (Models.ContainsKey(ii.ModelUri))
                    {
                        continue;
                    }

                    ModelInfo info = new ModelInfo()
                    {
                        NamespaceUri = ii.ModelUri,
                        NamespaceIndex = NamespaceUris.GetIndexOrAppend(ii.ModelUri),
                        NodeSetPath = filePath,
                        Version = (String.IsNullOrEmpty(ii.Version)) ? "1.00" : ii.Version,
                        PublicationDate = (ii.PublicationDateSpecified) ? ii.PublicationDate : DateTime.MinValue,
                        RepositoryPath = repositoryPath,
                        BaseWebUrl = baseWebUrl,
                        NodeSet = nodeset,
                        Types = new Dictionary<string, UANode>()
                    };

                    Models[ii.ModelUri] = info;
                    namespaceUri = ii.ModelUri;

                    if (DefaultModel == null)
                    {
                        DefaultModel = info;
                    }

                    while (ModelNamespaceIndexes.Count <= info.NamespaceIndex) ModelNamespaceIndexes.Add(null);
                    ModelNamespaceIndexes[info.NamespaceIndex] = info;

                    bool hasCoreModel = false;

                    if (ii.RequiredModel != null)
                    {
                        foreach (var jj in ii.RequiredModel)
                        {
                            if (jj.ModelUri == Opc.Ua.Namespaces.OpcUa)
                            {
                                hasCoreModel = true;
                            }

                            LoadModel(jj, info);
                        }
                    }

                    if (!hasCoreModel)
                    {
                        LoadModel(new ModelTableEntry()
                        {
                            ModelUri = Opc.Ua.Namespaces.OpcUa,
                        },
                        null);
                    }
                }
            }

            // index the namespaces.
            List<ushort> namespaceMappings = new List<ushort>();
            namespaceMappings.Add(0);

            if (nodeset.NamespaceUris != null && nodeset.NamespaceUris.Length > 0)
            {
                if (namespaceUri == null)
                {
                    namespaceUri = nodeset.NamespaceUris[0];
                }

                for (int ii = 0; ii < nodeset.NamespaceUris.Length; ii++)
                {
                    namespaceMappings.Add(NamespaceUris.GetIndexOrAppend(nodeset.NamespaceUris[ii]));
                }
            }

            // index the server uris.
            List<uint> serverUriMappings = new List<uint>();

            if (nodeset.ServerUris != null && nodeset.ServerUris.Length > 0)
            {
                for (int ii = 0; ii < nodeset.ServerUris.Length; ii++)
                {
                    serverUriMappings.Add(ServerUris.GetIndexOrAppend(nodeset.ServerUris[ii]));
                }
            }

            // index the aliases used in this file.
            Dictionary<string, NodeId> aliases = new Dictionary<string, NodeId>();

            if (nodeset.Aliases != null && nodeset.Aliases.Length > 0)
            {
                foreach (var ii in nodeset.Aliases)
                {
                    NodeId nodeId = NodeId.Parse(ii.Value);
                    nodeId = new NodeId(nodeId.Identifier, namespaceMappings[nodeId.NamespaceIndex]);
                    aliases[ii.Alias] = nodeId;
                }
            }

            Context context = new Context()
            {
                NodeSet = nodeset,
                NamespaceMappings = namespaceMappings,
                ServerUriMappings = serverUriMappings,
                Aliases = aliases
            };

            if (nodeset.Items != null && nodeset.Items.Length > 0)
            {
                // index the nodes defined in the model.
                foreach (var ii in nodeset.Items)
                {
                    NodeId nodeId = ResolveNodeId(context, ii.NodeId);
                    Nodes[nodeId] = ii;

                    ii.DecodedNodeId = nodeId;
                    ii.DecodedBrowseName = ResolveBrowseName(context, ii.BrowseName);

                    if (ii is UAObject)
                    {
                        ii.NodeClass = NodeClass.Object;
                    }
                    else if (ii is UAMethod)
                    {
                        ii.NodeClass = NodeClass.Method;
                    }
                    else if (ii is UAView)
                    {
                        ii.NodeClass = NodeClass.View;
                    }
                    else if (ii is UAObjectType)
                    {
                        ii.NodeClass = NodeClass.ObjectType;
                    }
                    else if (ii is UADataType)
                    {
                        ii.NodeClass = NodeClass.DataType;
                    }
                    else if (ii is UAReferenceType)
                    {
                        ii.NodeClass = NodeClass.ReferenceType;
                    }

                    var variable = ii as UAVariable;

                    if (variable != null)
                    {
                        nodeId = ResolveNodeId(context, variable.DataType);
                        variable.DecodedDataType = nodeId;
                        variable.NodeClass = NodeClass.Variable;
                        
                        if (variable.Value != null)
                        {
                            XmlDecoder decoder = CreateDecoder(context, variable.Value);
                            Opc.Ua.TypeInfo typeInfo = null;
                            variable.DecodedValue = new Variant(decoder.ReadVariantContents(out typeInfo));
                            decoder.Close();
                        }
                    }

                    var variableType = ii as UAVariableType;

                    if (variableType != null)
                    {
                        nodeId = ResolveNodeId(context, variableType.DataType);
                        variableType.DecodedDataType = nodeId;
                        variableType.NodeClass = NodeClass.VariableType;

                        if (variableType.Value != null)
                        {
                            XmlDecoder decoder = CreateDecoder(context, variableType.Value);
                            Opc.Ua.TypeInfo typeInfo = null;
                            variableType.DecodedValue = new Variant(decoder.ReadVariantContents(out typeInfo));
                            decoder.Close();
                        }
                    }

                    var dataType = ii as UADataType;

                    if (dataType != null)
                    {
                        if (dataType.Definition != null && dataType.Definition.Field != null)
                        {
                            foreach (var field in dataType.Definition.Field)
                            {
                                nodeId = ResolveNodeId(context, field.DataType);
                                field.DecodedDataType = nodeId;
                            }
                        }
                    }

                    var type = ii as UAType;

                    if (type != null)
                    {
                        var model = ModelNamespaceIndexes[ii.DecodedNodeId.NamespaceIndex];
                        model.Types[type.DecodedBrowseName.Name] = type;

                        if (!String.IsNullOrEmpty(type.SymbolicName) && type.SymbolicName != type.DecodedBrowseName.Name)
                        {
                            model.Types[type.SymbolicName] = type;
                        }
                    }
                }

                // index the references from the nodes.
                foreach (var ii in Nodes)
                {
                    if (!IsInTargetModel(context, ii.Key))
                    {
                        continue;
                    }

                    var node = ii.Value;

                    // check if the references need to be indexed.
                    if (node.References != null && node.References.Length > 0)
                    {
                        List<ReferenceInfo> references = null;

                        if (!References.TryGetValue(ii.Key, out references))
                        {
                            References[ii.Key] = references = new List<ReferenceInfo>();

                            foreach (var jj in node.References)
                            {
                                NodeId targetId = ResolveNodeId(context, jj.Value);
                                NodeId referenceTypeId = ResolveNodeId(context, jj.ReferenceType);

                                var reference = new ReferenceInfo()
                                {
                                    SourceId = ii.Key,
                                    TargetId = targetId,
                                    ReferenceTypeId = referenceTypeId,
                                    IsForward = jj.IsForward
                                };

                                references.Add(reference);
                            }
                        }

                        // ensure any reverse reference exists.
                        foreach (var jj in references)
                        {
                            if (jj.SourceId != jj.TargetId)
                            {
                                EnsureInverseReferenceExists(context, jj);
                            }
                        }
                    }
                }
            }

            return namespaceUri;
        }

        public ModelInfo GetModelInfo(string namespaceUri)
        {
            ModelInfo info = null;

            if (Models.TryGetValue(namespaceUri, out info))
            {
                return info;
            }

            return null;
        }

        public void SaveNodeSet(string modelUri, string filePath)
        {
            ModelInfo info = null;

            if (Models.TryGetValue(modelUri, out info))
            {
                using (StreamWriter writer = new StreamWriter(filePath))
                {
                    XmlSerializer serializer = new XmlSerializer(typeof(UANodeSet));
                    serializer.Serialize(writer, info.NodeSet);
                }
            }
        }

        public string FindModelUri(NodeId targetId)
        {
            ModelInfo model = FindModel(targetId);

            if (model != null)
            {
                return model.NamespaceUri;
            }

            return null;
        }

        public ModelInfo FindModelByReleaseInfo(string release)
        {
            if (String.IsNullOrEmpty(release))
            {
                return null;
            }

            foreach (var ii in ModelNamespaceIndexes)
            {
                if (release.StartsWith(ii.ModelName + " ")) 
                {
                    return ii;
                }
            }

            return null;
        }

        public ModelInfo FindModel(NodeId targetId = null)
        {
            if (targetId == null)
            {
                return DefaultModel;
            }

            if (targetId.NamespaceIndex >= ModelNamespaceIndexes.Count)
            {
                return null;
            }

            return ModelNamespaceIndexes[targetId.NamespaceIndex];
        }

        public T FindNode<T>(NodeId sourceId) where T : UANode
        {
            UANode node = null;

            if (sourceId == null || !Nodes.TryGetValue(sourceId, out node))
            {
                return default(T);
            }

            return node as T;
        }

        public NodeId FindFirstTarget(NodeId sourceId, NodeId referenceTypeId, bool isForward, string targetName)
        {
            NodeId targetId = null;

            int index = targetName.IndexOf(":");

            if (index > 0)
            {
                var ns = targetName.Substring(0, index);

                ushort namespaceIndex = 0;

                if (UInt16.TryParse(ns, out namespaceIndex))
                {
                    targetName = targetName.Substring(index + 1);

                    targetId = FindFirstTarget(sourceId, referenceTypeId, isForward, new QualifiedName(targetName, namespaceIndex));

                    if (targetId != null)
                    {
                        return targetId;
                    }
                }
            }

            for (int ii = NamespaceUris.Count - 1; targetId == null && ii >= 0; ii--)
            {
                targetId = FindFirstTarget(sourceId, referenceTypeId, isForward, new QualifiedName(targetName, (ushort)ii));
            }

            return targetId;
        }

        public NodeId FindFirstTarget(NodeId sourceId, NodeId referenceTypeId, bool isForward, bool includeSubtypes, QualifiedName targetName = null)
        {
            List<ReferenceInfo> references = null;

            if (!References.TryGetValue(sourceId, out references))
            {
                return null;
            }

            foreach (var ii in references)
            {
                if (isForward == ii.IsForward)
                {
                    if (!NodeId.IsNull(referenceTypeId))
                    {
                        if (!includeSubtypes && referenceTypeId != ii.ReferenceTypeId)
                        {
                            continue;
                        }

                        if (includeSubtypes && !IsTypeOf(ii.ReferenceTypeId, referenceTypeId))
                        {
                            continue;
                        }
                    }

                    if (targetName != null)
                    {
                        UANode target = null;

                        if (Nodes.TryGetValue(ii.TargetId, out target))
                        {
                            if (target.DecodedBrowseName == targetName)
                            {
                                return ii.TargetId;
                            }
                        }

                        continue;
                    }

                    return ii.TargetId;
                }
            }

            return null;
        }


        public NodeId FindFirstTarget(NodeId sourceId, NodeId referenceTypeId, bool isForward, QualifiedName targetName = null)
        {
            return FindFirstTarget(sourceId, referenceTypeId, isForward, false, targetName);
        }

        public List<ReferenceInfo> FindReferences(NodeId sourceId)
        {
            List<ReferenceInfo> references = null;

            if (sourceId != null && References.TryGetValue(sourceId, out references))
            {
                return references;
            }

            return null;
        }

        public Variant GetVariableValue(NodeId sourceId)
        {
            UANode node = null;

            if (!Nodes.TryGetValue(sourceId, out node))
            {
                return Variant.Null;
            }

            UAVariable variable = node as UAVariable;

            if (variable != null)
            {
                return variable.DecodedValue;
            }

            UAVariableType vt = node as UAVariableType;

            if (vt != null)
            {
                return vt.DecodedValue;
            }

            return Variant.Null;
        }

        public UANode FindNodeByName(string name, bool searchInstances = false, bool targetModelOnly = false, int targetModelNamespaceIndex = -1)
        {
            if (String.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            int namespaceIndex = targetModelNamespaceIndex;

            int index = name.IndexOf(":");

            if (index > 0)
            {
                if (!Int32.TryParse(name.Substring(0, index), out namespaceIndex))
                {
                    namespaceIndex = targetModelNamespaceIndex;
                }
                else
                {
                    name = name.Substring(index + 1);
                }
            }

            UANode node = null;
            ModelInfo model = null;

            if (namespaceIndex >= 0)
            {
                if (ModelNamespaceIndexes.Count > namespaceIndex)
                {
                    model = ModelNamespaceIndexes[namespaceIndex];

                    if (model.Types.TryGetValue(name, out node))
                    {
                        return node;
                    }

                    if (searchInstances)
                    {
                        foreach (var ii in model.NodeSet.Items)
                        {
                            if (ii.DecodedBrowseName.NamespaceIndex == namespaceIndex && ii.DecodedBrowseName.Name == name)
                            {
                                return ii;
                            }
                        }
                    }

                    return null;
                }
            }

            model = FindModel(null);

            if (model.Types.TryGetValue(name, out node))
            {
                return node;
            }

            if (!targetModelOnly)
            {
                for (int ii = NamespaceUris.Count - 1; ii >= 0; ii--)
                {
                    if (ii != model.NamespaceIndex && ModelNamespaceIndexes.Count > ii)
                    {
                        if (ModelNamespaceIndexes[ii].Types.TryGetValue(name, out node))
                        {
                            return node;
                        }
                    }
                }
            }

            if (searchInstances)
            {
                node = SearchForInstanceByName(name);

                if (node != null)
                {
                    return node;
                }
            }

            return null;
        }
        public BuiltInType GetBuiltInType(NodeId typeId)
        {
            if (NodeId.IsNull(typeId))
            {
                return BuiltInType.Variant;
            }

            NodeId targetId = null;
            BuiltInType type = BuiltInType.Null;

            do
            {
                targetId = FindFirstTarget(typeId, Opc.Ua.ReferenceTypeIds.HasSubtype, false);

                if (targetId == null)
                {
                    return BuiltInType.Variant;
                }

                type = Opc.Ua.TypeInfo.GetBuiltInType(targetId);

                if (type != BuiltInType.Null)
                {
                    return type;
                }

                typeId = targetId;
            }
            while (!NodeId.IsNull(targetId));

            return type;
        }

        public bool IsTypeOf(NodeId subtypeId, NodeId nodeId)
        {
            if (NodeId.IsNull(subtypeId) || NodeId.IsNull(nodeId))
            {
                return false;
            }

            if (subtypeId == nodeId)
            {
                return true;
            }

            NodeId targetId = null;

            do
            {
                targetId = FindFirstTarget(subtypeId, Opc.Ua.ReferenceTypeIds.HasSubtype, false);

                if (targetId == nodeId)
                {
                    return true;
                }

                subtypeId = targetId;
            }
            while (!NodeId.IsNull(targetId));

            return false;
        }

        private bool IsStandAloneInstance(NodeId nodeId)
        {
            var modellingRuleId = FindFirstTarget(nodeId, Opc.Ua.ReferenceTypeIds.HasModellingRule, true);

            if (!NodeId.IsNull(modellingRuleId))
            {
                return false;
            }

            foreach (var reference in FindReferences(nodeId))
            {
                if (reference.IsForward)
                {
                    continue;
                }

                if (!IsTypeOf(reference.ReferenceTypeId, Opc.Ua.ReferenceTypeIds.HierarchicalReferences))
                {
                    continue;
                }

                var parent = FindNode<UANode>(reference.TargetId);

                if (parent == null)
                {
                    return false;
                }

                if (parent.NodeClass != NodeClass.Object)
                {
                    return false;
                }

                modellingRuleId = FindFirstTarget(parent.DecodedNodeId, Opc.Ua.ReferenceTypeIds.HasModellingRule, true);
                
                if (!NodeId.IsNull(modellingRuleId))
                {
                    return false;
                }
            }

            return true;
        }

        public UANode SearchForInstanceByName(string name)
        {
            ushort namespaceIndex = this.DefaultModel.NamespaceIndex;

            foreach (var node in this.Nodes.Values)
            {
                if (namespaceIndex == node.DecodedBrowseName.NamespaceIndex && node.DecodedBrowseName.Name == name)
                {
                    if (IsStandAloneInstance(node.DecodedNodeId))
                    {
                        return node;
                    }
                }
            }

            return null;
        }

        public static string CheckNodeSet(string path)
        {
            ModelManager manager = new ModelManager();
            var nodeset = manager.Read(path);

            if (nodeset.Models != null && nodeset.Models.Length > 0)
            {
                return nodeset.Models[0].ModelUri;
            }

            if (nodeset.NamespaceUris != null && nodeset.NamespaceUris.Length > 0)
            {
                return nodeset.NamespaceUris[0];
            }

            return Namespaces.OpcUa;
        }

        private static void GetRequiredModels(ModelTableEntry model, HashSet<string> uris)
        {
            if (model.RequiredModel != null)
            {
                foreach (var ii in model.RequiredModel)
                {
                    uris.Add(ii.ModelUri);
                    GetRequiredModels(ii, uris);
                }
            }
        }

        public static List<string> GetRequiredModelUris(string path)
        {
            ModelManager manager = new ModelManager();
            var nodeset = manager.Read(path);

            HashSet<string> uris = new HashSet<string>();

            if (nodeset.Models != null && nodeset.Models.Length > 0)
            {
                foreach (var ii in nodeset.Models)
                {
                    GetRequiredModels(ii, uris);
                }
            }

            if (uris.Count == 0 && nodeset.NamespaceUris != null && nodeset.NamespaceUris.Length > 0)
            {
                uris.Add(Opc.Ua.Namespaces.OpcUa);
            }

            return uris.ToList<string>();
        }

        public static UANodeSet Read(Stream istrm)
        {
            using (StreamReader reader = new StreamReader(istrm))
            {
                XmlSerializer serializer = new XmlSerializer(typeof(UANodeSet));

                var nodeset = serializer.Deserialize(reader) as UANodeSet;

                if (nodeset.Models == null)
                {
                    nodeset.Models = new ModelTableEntry[] 
                    {
                        new ModelTableEntry()
                        {
                            ModelUri = nodeset.NamespaceUris[0],
                            RequiredModel = new ModelTableEntry[]
                            {
                                new ModelTableEntry() { ModelUri = Namespaces.OpcUa }
                            }
                        }
                    };
                }

                return nodeset;
            }
        }

        private UANodeSet Read(string filePath)
        {
            using (Stream istrm = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                return Read(istrm);
            }
        }

        private UANodeSet Read(Assembly assembly, string resourceName)
        {
            if (assembly == null)
            {
                assembly = Assembly.GetCallingAssembly();
            }

            foreach (var ii in assembly.GetManifestResourceNames())
            {
                if (ii.EndsWith(resourceName, StringComparison.OrdinalIgnoreCase))
                {
                    using (Stream istrm = assembly.GetManifestResourceStream(ii))
                    {
                        return Read(istrm);
                    }
                }
            }

            throw new FileNotFoundException($"{assembly.FullName}.{resourceName}");
        }

        private NodeId ResolveNodeId(Context context, string sourceId)
        {
            NodeId nodeId = null;

            if (!context.Aliases.TryGetValue(sourceId, out nodeId))
            {
                nodeId = NodeId.Parse(sourceId);
                nodeId = new NodeId(nodeId.Identifier, context.NamespaceMappings[nodeId.NamespaceIndex]);
                context.Aliases[sourceId] = nodeId;
            }

            return nodeId;
        }

        private QualifiedName ResolveBrowseName(Context context, string sourceName)
        {
            QualifiedName browseName = QualifiedName.Parse(sourceName);

            if (browseName.NamespaceIndex != 0)
            {
                browseName = new QualifiedName(browseName.Name, context.NamespaceMappings[browseName.NamespaceIndex]);
            }

            return browseName;
        }

        private void EnsureInverseReferenceExists(Context context, ReferenceInfo reference)
        {
            // look up indexed references for the target,
            List<ReferenceInfo> references = null;

            if (!References.TryGetValue(reference.TargetId, out references))
            {
                References[reference.TargetId] = references = new List<ReferenceInfo>();

                // index references if they have not been indexed yet.
                UANode target = null;

                if (Nodes.TryGetValue(reference.TargetId, out target))
                {
                    if (target.References != null && target.References.Length > 0)
                    {
                        foreach (var ii in target.References)
                        {
                            var targetId = ResolveNodeId(context, ii.Value);
                            var referenceTypeId = ResolveNodeId(context, ii.ReferenceType);

                            references.Add(new ReferenceInfo()
                            {
                                SourceId = reference.TargetId,
                                TargetId = targetId,
                                ReferenceTypeId = referenceTypeId,
                                IsForward = ii.IsForward
                            });
                        }
                    }
                }
            }

            // search for existing reference.
            foreach (var ii in references)
            {
                if (reference.IsForward != ii.IsForward && reference.ReferenceTypeId == ii.ReferenceTypeId && reference.SourceId == ii.TargetId)
                {
                    return;
                }
            }

            // add inverse reference.
            references.Add(new ReferenceInfo()
            {
                SourceId = reference.TargetId,
                TargetId = reference.SourceId,
                ReferenceTypeId = reference.ReferenceTypeId,
                IsForward = !reference.IsForward
            });
        }

        private bool IsInTargetModel(Context context, NodeId nodeId)
        {
            if (nodeId == null)
            {
                return false;
            }

            if (context.NodeSet.Models != null)
            {
                foreach (var jj in context.NodeSet.Models)
                {
                    ModelInfo model = null;

                    if (Models.TryGetValue(jj.ModelUri, out model))
                    {
                        if (nodeId.NamespaceIndex == model.NamespaceIndex)
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        private void LoadModel(ModelTableEntry required, ModelInfo source)
        {
            ModelInfo model = null;

            if (Models.TryGetValue(required.ModelUri, out model))
            {
                if (required.PublicationDateSpecified && required.PublicationDate <= model.PublicationDate)
                {
                    return;
                }
            }

            var callback = ModelRequired;

            if (callback != null)
            {
                var args = new ModelRequiredEventArgs(required.ModelUri, required.PublicationDate, source??DefaultModel);
                callback(this, args);

                if (args.ModelFilePath != null)
                {
                    LoadModel(args.ModelFilePath, args.RepositoryPath, args.BaseWebUrl);
                }
            }
        }

        private XmlDecoder CreateDecoder(Context context, XmlElement source)
        {
            ServiceMessageContext messageContext = new ServiceMessageContext();
            messageContext.NamespaceUris = this.NamespaceUris;
            messageContext.ServerUris = ServerUris;

            XmlDecoder decoder = new XmlDecoder(source, messageContext);

            NamespaceTable namespaceUris = new NamespaceTable();

            if (context.NodeSet.NamespaceUris != null)
            {
                for (int ii = 0; ii < context.NodeSet.NamespaceUris.Length; ii++)
                {
                    namespaceUris.Append(context.NodeSet.NamespaceUris[ii]);
                }
            }

            StringTable serverUris = new StringTable();

            if (context.NodeSet.ServerUris != null)
            {
                for (int ii = 0; ii < context.NodeSet.ServerUris.Length; ii++)
                {
                    serverUris.Append(context.NodeSet.ServerUris[ii]);
                }
            }

            decoder.SetMappingTables(namespaceUris, serverUris);

            return decoder;
        }

        private string GetPath(string rootPath, UANode node)
        {
            string prefix = String.Empty;

            if (node is UAObjectType)
            {
                prefix += "ObjectTypes";
            }
            else if (node is UAVariableType)
            {
                prefix += "VariableTypes";
            }
            else if (node is UADataType)
            {
                prefix += "DataTypes";
            }
            else if (node is UAReferenceType)
            {
                prefix += "ReferenceTypes";
            }
            else
            {
                return null;
            }

            var path = Path.Combine(rootPath, prefix, node.BrowseName);
            path = path.Replace("\\", "/");
            return path;
        }
    }

    public class ReferenceInfo
    {
        public NodeId SourceId;
        public NodeId TargetId;
        public NodeId ReferenceTypeId;
        public bool IsForward;

        public override string ToString()
        {
            if (IsForward)
            {
                return $"{SourceId}=>{TargetId}";
            }

            return $"{SourceId}<={TargetId}";
        }
    }

    public class ModelInfo
    {
        public string ModelName;
        public string NamespaceUri;
        public ushort NamespaceIndex;
        public string NodeSetPath;
        public string RepositoryPath;
        public string BaseWebUrl;
        public string Version;
        public DateTime PublicationDate;
        public UANodeSet NodeSet;
        public Dictionary<string, UANode> Types;

        public override string ToString()
        {
            return $"{ModelName} {NamespaceUri}";
        }
    }

    public class ModelRequiredEventArgs : EventArgs
    {
        internal ModelRequiredEventArgs(string modelUri, DateTime publicationDate, ModelInfo defaultModel)
        {
            ModelUri = modelUri;
            PublicationDate = publicationDate;
            RepositoryPath = String.Empty;
            BaseWebUrl = String.Empty;
            DefaultModel = defaultModel;
        }

        public ModelInfo DefaultModel { get; }

        public string ModelUri { get; }

        public DateTime PublicationDate { get; }

        public string ModelFilePath { get; set; }

        public string RepositoryPath { get; set; }

        public string BaseWebUrl { get; set; }
    }
}