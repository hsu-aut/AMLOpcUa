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
using Opc.Ua;
using Aml.Engine.AmlObjects;
using Aml.Engine.CAEX;
using Aml.Engine.CAEX.Extensions;
using UADataType = MarkdownProcessor.NodeSet.UADataType;
using UAInstance = MarkdownProcessor.NodeSet.UAInstance;
using UANode = MarkdownProcessor.NodeSet.UANode;
using UAObject = MarkdownProcessor.NodeSet.UAObject;
using UAType = MarkdownProcessor.NodeSet.UAType;
using UAVariable = MarkdownProcessor.NodeSet.UAVariable;
using UAVariableType = MarkdownProcessor.NodeSet.UAVariableType;
using DataTypeField = MarkdownProcessor.NodeSet.DataTypeField;
using Aml.Engine.Adapter;
using System.Xml.Linq;
using System.Linq;
using System.Diagnostics;
using System.Net;
using NodeSetToAmlUtils;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Xml;
using Opc2Aml;


namespace MarkdownProcessor
{
    public class NodeSetToAML
    {
        private const string ATLPrefix = "ATL_";
        private const string ICLPrefix = "ICL_";
        private const string RCLPrefix = "RCL_";
        private const string SUCPrefix = "SUC_";
        private const string ListOf = "ListOf";
        private const string MetaModelName = "OpcAmlMetaModel";
        private const string UaBaseRole = "UaBaseRole";
        private const string MethodNodeClass = "UaMethodNodeClass";
        private const string RefClassConnectsToPath = "RefClassConnectsToPath";
        private const string NonHierarchicalReferenceAttribute = "ReferenceIds";
        private const string IsSource = "IsSource";
        private const string ForwardPrefix = "f";
        private const string ReversePrefix = "r";
        private const string RoleClassPrefix = "rc";
        private const string Enumeration = "Enumeration";
        private const string KeepExternalInterfaceAttribute = "KeepExternalInterface";
        private ModelManager m_modelManager;
        private CAEXDocument m_cAEXDocument;
        private AttributeTypeLibType m_atl_temp;
        private readonly NodeId PropertyTypeNodeId = new NodeId(68, 0);
        private readonly NodeId HasSubTypeNodeId = new NodeId(45, 0);
        private readonly NodeId HasPropertyNodeId = new NodeId(46, 0);
        private readonly NodeId QualifiedNameNodeId = new NodeId(20, 0);
        private readonly NodeId NodeIdNodeId = new NodeId(17, 0);
        private readonly NodeId ExpandedNodeIdNodeId = new NodeId(18, 0);
        private readonly NodeId RelativePathElementNodeId = new NodeId(537, 0);
        private readonly NodeId RelativePathNodeId = new NodeId(540, 0);

        private readonly NodeId GuidNodeId = new NodeId(14, 0);
        private readonly NodeId BaseDataTypeNodeId = new NodeId(24, 0);
        private readonly NodeId NumberNodeId = new NodeId(26, 0);
        private readonly NodeId OptionSetStructureNodeId = new NodeId(12755, 0);
        private readonly NodeId AggregatesNodeId = new NodeId(44, 0);
        private readonly NodeId HasInterfaceNodeId = new NodeId(17603, 0);
        private readonly NodeId HasTypeDefinitionNodeId = new NodeId(40, 0);
        private readonly NodeId HasModellingRuleNodeId = new NodeId(37, 0);
        private readonly NodeId BaseInterfaceNodeId = new NodeId(17602, 0);
        private readonly NodeId RootNodeId = new NodeId(84, 0);
        private readonly NodeId HierarchicalNodeId = new NodeId(33, 0);
        private readonly NodeId OrganizesNodeId = new NodeId(35, 0);
        private readonly NodeId TypesFolderNodeId = new NodeId(86, 0);
        private readonly NodeId TwoStateDiscreteNodeId = new NodeId(2373, 0);
        private readonly NodeId MultiStateDiscreteNodeId = new NodeId(2376, 0);
        private readonly NodeId MultiStateValueDiscreteNodeId = new NodeId(11238, 0);
        private readonly NodeId EnumerationNodeId = Opc.Ua.DataTypeIds.Enumeration;

        private readonly NodeId IntegerNodeId = Opc.Ua.DataTypeIds.Integer;

        private readonly System.Xml.Linq.XNamespace defaultNS = "http://www.dke.de/CAEX";
        private const string OpcUaTypeOnly = "OpcUa:TypeOnly";
        private const string uaNamespaceURI = "http://opcfoundation.org/UA/";
        private const string OpcLibInfoNamespace = "http://opcfoundation.org/UA/FX/2021/08/OpcUaLibInfo.xsd";
        private UANode structureNode;
        private readonly List<string> ExcludedDataTypeList = new List<string>() { "InstanceNode", "TypeNode" };
        private Dictionary<string, Dictionary<string,string>> LookupNames = new Dictionary<string, Dictionary<string, string>>();
        private HashSet<string> ExtensionObjectExclusions = null;

        private Dictionary<string, Dictionary<string, DataTypeField>> ReferenceAttributeMap = 
            new Dictionary<string, Dictionary<string, DataTypeField>>();
        private readonly string[] NodeId_IdAttributeNames = { "NumericId", "StringId", "GuidId", "OpaqueId" };

        private List<string> PreventInfiniteRecursionList = new List<string>();
        private const int ua2xslookup_count = 18;
        private const int ua2xslookup_uaname = 0;
        private const int ua2xslookup_xsname = 1;
        private readonly string[,] ua2xsLookup = new string[ua2xslookup_count, 2] {
            { "Boolean" , "xs:boolean"  },
            { "SByte" , "xs:Byte"  },
            { "Byte" , "xs:unsignedByte"  },
            { "Int16" , "xs:short"  },
            { "UInt16" , "xs:unsignedShort"  },
            { "Int32" , "xs:int"  },
            { "UInt32" , "xs:unsignedInt"  },
            { "Int64" , "xs:long"  },
            { "UInt64" , "xs:unsignedLong"  },
            { "Float" , "xs:float"  },
            { "Double" , "xs:double"  },
            { "String" , "xs:string"  },
            { "DateTime" , "xs:dateTime"  },
            { "ByteString" , "xs:base64Binary"  },
            { "LocalizedText" , "xs:string"  },
            { "QualifiedName" , "xs:anyURI"  },
            { "StatusCode" , "xs:unsignedInt"  },
            { Enumeration , "xs:int"  }
        };

        private bool _runningInstances = false;
        private NonHierarchicalReferences _nonHierarchicalReferences = null;

        Dictionary<NodeId, List<ReferenceInfo>> m_multipleReferences = new Dictionary<NodeId, List<ReferenceInfo>>();
        Dictionary<NodeId, InternalElementType> m_instances = new Dictionary<NodeId, InternalElementType>();

        public NodeSetToAML(ModelManager modelManager)
        {
            m_modelManager = modelManager;
            _nonHierarchicalReferences = new NonHierarchicalReferences(modelManager);
        }

        public T FindNode<T>(NodeId sourceId) where T : UANode 
        {
            UANode node = null;
            node = m_modelManager.FindNode<T>(sourceId);
            // display error message and throw
            if( node == null )
                throw new Exception( "Can't find node: " + sourceId.ToString() + "   Is your nodeset file missing a <RequiredModel> element?");
            return node as T;
        }

        public void CreateAML(string modelPath, string modelName = null)
        {
            DateTime startTime = DateTime.UtcNow;
            string modelUri = m_modelManager.LoadModel(modelPath, null, null);
            structureNode = m_modelManager.FindNodeByName("Structure");
            if (modelName == null)
                modelName = modelPath;

            Utils.LogInfo( "CreateAML for model {0}", modelName );

            m_cAEXDocument = CAEXDocument.New_CAEXDocument();
            var cAEXDocumentTemp = CAEXDocument.New_CAEXDocument();

            // adds the base libraries to the document
            AutomationMLInterfaceClassLibType.InterfaceClassLib(m_cAEXDocument);
            AutomationMLBaseRoleClassLibType.RoleClassLib(m_cAEXDocument);
            AutomationMLBaseAttributeTypeLibType.AttributeTypeLib(m_cAEXDocument);
            AutomationMLBaseAttributeTypeLibType.AttributeTypeLib(cAEXDocumentTemp);
            AutomationMLInterfaceClassLibType.InterfaceClassLib(cAEXDocumentTemp);

            // process each model in  order (with the base UA Model first )

            AddMetaModelLibraries(m_cAEXDocument);


            List<ModelInfo> MyModelInfoList = new List<ModelInfo>();
            // Get the models in the correct order for processing the base models first
            foreach (var modelInfo in m_modelManager.ModelNamespaceIndexes)
            {
                if (modelInfo != m_modelManager.DefaultModel)
                    MyModelInfoList.Add(modelInfo);
            }
            MyModelInfoList.Add(m_modelManager.DefaultModel);

            OrderModelInfo orderModelInfo = new OrderModelInfo();
            List<ModelInfo> orderedModelList = orderModelInfo.GetProcessOrder(MyModelInfoList);

            foreach( var modelInfo in orderedModelList )
            {
                Utils.LogInfo( "{0} processing model {1} NamespaceIndex {2}", 
                    modelName, modelInfo.NamespaceUri, modelInfo.NamespaceIndex );

                AttributeTypeLibType atl = null;
                InterfaceClassLibType icl = null;
                RoleClassLibType rcl = null;
                SystemUnitClassLibType scl = null;

                m_atl_temp = cAEXDocumentTemp.CAEXFile.AttributeTypeLib.Append(ATLPrefix + modelInfo.NamespaceUri);

                AddLibraryHeaderInfo(m_atl_temp as CAEXBasicObject, modelInfo);

                // create the InterfaceClassLibrary
                var icl_temp = cAEXDocumentTemp.CAEXFile.InterfaceClassLib.Append(ICLPrefix + modelInfo.NamespaceUri);
                AddLibraryHeaderInfo(icl_temp as CAEXBasicObject, modelInfo);

                // Create the RoleClassLibrary
                var rcl_temp = cAEXDocumentTemp.CAEXFile.RoleClassLib.Append(RCLPrefix + modelInfo.NamespaceUri);
                // var rcl_temp = m_cAEXDocument.CAEXFile.RoleClassLib.Append(RCLPrefix + modelInfo.NamespaceUri);
                AddLibraryHeaderInfo(rcl_temp as CAEXBasicObject, modelInfo);

                // Create the SystemUnitClassLibrary
                var scl_temp =  cAEXDocumentTemp.CAEXFile.SystemUnitClassLib.Append(SUCPrefix + modelInfo.NamespaceUri);
                // var scl_temp = m_cAEXDocument.CAEXFile.SystemUnitClassLib.Append(SUCPrefix + modelInfo.NamespaceUri);
                AddLibraryHeaderInfo(scl_temp as CAEXBasicObject, modelInfo);

                SortedDictionary<string, AttributeFamilyType> SortedDataTypes = new SortedDictionary<string, AttributeFamilyType>();

                SortedDictionary<string, NodeId> SortedReferenceTypes = new SortedDictionary<string, NodeId>();
                SortedDictionary<string, NodeId> SortedObjectTypes = new SortedDictionary<string, NodeId>();  // also contains VariableTypes

                Dictionary<string, UANode> fieldDefinitions = new Dictionary<string, UANode>();
                foreach (KeyValuePair<string, UANode> node in modelInfo.Types)
                {
                    switch (node.Value.NodeClass)
                    {
                        case NodeClass.DataType:

                            AttributeFamilyType toAdd = ProcessDataType( node.Value );
                            if( toAdd != null )
                            {
                                if( !SortedDataTypes.ContainsKey( node.Value.DecodedBrowseName.Name ) )
                                {
                                    SortedDataTypes.Add( node.Value.DecodedBrowseName.Name, toAdd );
                                    fieldDefinitions.Add( node.Value.DecodedBrowseName.Name, node.Value );
                                }
                            }
                            break;
                        case NodeClass.ReferenceType:
                            SortedReferenceTypes.Add(node.Value.DecodedBrowseName.Name, node.Value.DecodedNodeId);
                            break;
                        case NodeClass.ObjectType:
                        case NodeClass.VariableType:
                            if (!SortedObjectTypes.ContainsKey(node.Value.DecodedBrowseName.Name))
                                SortedObjectTypes.Add(node.Value.DecodedBrowseName.Name, node.Value.DecodedNodeId);
                            break;
                    }
                }

                foreach( KeyValuePair<string, AttributeFamilyType> dicEntry in SortedDataTypes)
                {
                    if (atl == null)
                    {
                        atl = m_cAEXDocument.CAEXFile.AttributeTypeLib.Append(ATLPrefix + modelInfo.NamespaceUri);
                        AddLibraryHeaderInfo(atl as CAEXBasicObject, modelInfo);
                    }

                    atl.AttributeType.Insert( dicEntry.Value, asFirst: false, asIs: true );
                }

                foreach( var dicEntry in SortedDataTypes)  // cteate the ListOf versions
                {
                    atl.AttributeType.Insert(CreateListOf(dicEntry.Value), false);  // insert into the AML document in alpha order
                }

                if( atl != null )
                {
                    foreach( AttributeFamilyType attribute in atl.AttributeType )
                    {
                        if( fieldDefinitions.ContainsKey( attribute.Name ) )
                        {
                            AddAttributeData( attribute, fieldDefinitions[ attribute.Name ] );
                        }
                    }
                }


                foreach( var refType in SortedReferenceTypes)
                {
                    ProcessReferenceType(ref icl_temp, refType.Value);
                }

                // reorder icl_temp an put in the real icl in alpha order

                foreach( var refType in SortedReferenceTypes)
                {
                    string iclpath = BuildLibraryReference(ICLPrefix, modelInfo.NamespaceUri, refType.Key);
                    InterfaceClassType ict = icl_temp.CAEXDocument.FindByPath(iclpath) as InterfaceClassType;

                    if (ict != null)
                    {

                        if (icl == null)
                        {
                            // Create the InterfaceClassLibrary
                            icl = m_cAEXDocument.CAEXFile.InterfaceClassLib.Append(ICLPrefix + modelInfo.NamespaceUri);
                            AddLibraryHeaderInfo(icl as CAEXBasicObject, modelInfo);
                        }
                        icl.Insert(ict, asFirst: false, asIs: true);
                    }
                }

                Utils.LogInfo( "Processing SystemUnitClass Types" );

                foreach( var obType in SortedObjectTypes)
                {
                    FindOrAddSUC(ref scl_temp, ref rcl_temp, obType.Value);
                }

                // re-order the rcl_temp and scl_temp in alpha order into the real rcl and scl

                foreach( var obType in SortedObjectTypes)
                {
                    string sucpath = BuildLibraryReference(SUCPrefix, modelInfo.NamespaceUri, obType.Key);
                    SystemUnitFamilyType sft = scl_temp.CAEXDocument.FindByPath(sucpath) as SystemUnitFamilyType;
                    if (sft != null)
                    {
                        if (scl == null)
                        {
                            scl = m_cAEXDocument.CAEXFile.SystemUnitClassLib.Append(SUCPrefix + modelInfo.NamespaceUri);
                            AddLibraryHeaderInfo(scl as CAEXBasicObject, modelInfo);
                        }
                        scl.Insert(sft, asFirst: false, asIs: true );
                    }

                    string rclpath = BuildLibraryReference(RCLPrefix, modelInfo.NamespaceUri, obType.Key);
                    var rft = rcl_temp.CAEXDocument.FindByPath(rclpath) as RoleFamilyType;
                    if (rft != null)
                    {
                        if (rcl == null)
                        {
                            rcl = m_cAEXDocument.CAEXFile.RoleClassLib.Append(RCLPrefix + modelInfo.NamespaceUri);
                            AddLibraryHeaderInfo(rcl as CAEXBasicObject, modelInfo);
                        }
                        rcl.Insert(rft, asFirst: false, asIs: true );
                    }
                }
            }

            Utils.LogInfo( "Creating Instances" );

            _runningInstances = true;

            CreateInstances(); //  add the instances for each model

            Utils.LogInfo("Add NonHierarchical References");

            AddNonHierarchicalReferences();

            RemoveTypeOnly();

            Utils.LogDebug( "Remove Type Only information Complete" );

            // write out the AML file
            // var OutFilename = modelName + ".aml";
            // m_cAEXDocument.SaveToFile(OutFilename, true);
            FileInfo internalFileInfo = new FileInfo( modelName );
            FileInfo outputFileInfo = new FileInfo( modelName + ".amlx" );
            var container = new AutomationMLContainer(outputFileInfo.FullName, System.IO.FileMode.Create);
            container.AddRoot(m_cAEXDocument.SaveToStream(true), new Uri("/" + internalFileInfo.Name + ".aml", UriKind.Relative));
            container.Close();

            DateTime endTime = DateTime.UtcNow;
            TimeSpan totalTime = endTime - startTime;
            Utils.LogInfo( "Amlx Container Created for model " + modelName + 
                " (Time in creation " + totalTime.ToString() + ")" );
        }

        private void AddLibraryHeaderInfo(CAEXBasicObject bo, ModelInfo modelInfo = null)
        {
            bo.Description = "AutomationML Library to support OPC UA meta model";
            bo.Copyright = "\xA9 OPC Foundation";

            if (modelInfo != null)
            {

                XNamespace ns = OpcLibInfoNamespace;
                bo.Description = "AutomationML Library auto-generated from OPC UA Nodeset file for OPC UA Namepace: " + modelInfo.NamespaceUri;
                var ab = new XElement(ns + "OpcUaLibInfo");

                var ai = new XElement(ns + "OpcUaNamespaceUri", modelInfo.NamespaceUri);
                ab.Add(ai);

                ai = new XElement(ns + "ModelVersion", modelInfo.NodeSet.Models[0].Version);
                ab.Add(ai);

                ai = new XElement(ns + "ModelPublicationDate", modelInfo.PublicationDate);
                ab.Add(ai);

                bo.AdditionalInformation.Append(ab);
            }

            var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            var aj = new XElement(defaultNS + "Nodeset2AmlToolVersion");
            aj.SetValue(version);
            bo.AdditionalInformation.Append(aj);

            var buildDate = new DateTime(2000, 1, 1).AddDays(version.Build).AddSeconds(version.Revision * 2);
            aj = new XElement(defaultNS + "Nodeset2AmlToolBuildDate");
            aj.SetValue(buildDate.ToUniversalTime());
            bo.AdditionalInformation.Append(aj);
        }


        private AttributeValueRequirementType BuiltInTypeConstraint()
        {
            AttributeValueRequirementType avrt = new AttributeValueRequirementType(new System.Xml.Linq.XElement(defaultNS + "Constraint"));
            avrt.Name = "BuiltInType Constraint";
            var res = avrt.New_NominalType();

            foreach (string name in Enum.GetNames(typeof(BuiltInType)))
            {
                res.RequiredValue.Append(name);
            }

            return avrt;
        }

        private AttributeValueRequirementType AttributeIdConstraint()
        {
            AttributeValueRequirementType avrt = new AttributeValueRequirementType(new System.Xml.Linq.XElement(defaultNS + "Constraint"));
            avrt.Name = "AttributeId Constraint";
            var res = avrt.New_NominalType();

            foreach (string name in Attributes.GetBrowseNames())
            {
                res.RequiredValue.Append(name);
            }

            return avrt;
        }

        private void AddMetaModelLibraries(CAEXDocument doc)
        {
            // add ModelingRuleType (that does not exist in UA) to ATL


            AttributeTypeLibType atl_meta = doc.CAEXFile.AttributeTypeLib.Append(ATLPrefix + MetaModelName);
            AddLibraryHeaderInfo(atl_meta as CAEXBasicObject);

            var added = new AttributeFamilyType(new System.Xml.Linq.XElement(defaultNS + "AttributeType"));
            AttributeType added2 = null;
            var att = added as AttributeTypeType;
            added.Name = "ModellingRuleType";

            att.AttributeDataType = "xs:string";
            AttributeValueRequirementType avrt = new AttributeValueRequirementType(new System.Xml.Linq.XElement(defaultNS + "Constraint"));
            avrt.Name = added.Name + " Constraint";
            var res = avrt.New_NominalType();
            res.RequiredValue.Append("ExposesItsArray");
            res.RequiredValue.Append("Mandatory");
            res.RequiredValue.Append("MandatoryPlaceholder");
            res.RequiredValue.Append("Optional");
            res.RequiredValue.Append("OptionalPlaceholder");
            att.Constraint.Insert(avrt);
            atl_meta.AttributeType.Insert(added, false);

            // add BuiltInType enumeration (that does not exist in UA) to ATL

            added = new AttributeFamilyType(new System.Xml.Linq.XElement(defaultNS + "AttributeType"));
            att = added as AttributeTypeType;
            added.Name = "BuiltInType";
            att.AttributeDataType = "xs:string";
            att.Constraint.Insert(BuiltInTypeConstraint());
            atl_meta.AttributeType.Insert(added, false);

            // add AttributeId enumeration (that does not exist in UA) to ATL
            added = new AttributeFamilyType(new System.Xml.Linq.XElement(defaultNS + "AttributeType"));
            att = added as AttributeTypeType;
            added.Name = "AttributeId";
            att.AttributeDataType = "xs:string";
            att.Constraint.Insert(AttributeIdConstraint());
            atl_meta.AttributeType.Insert(added, false);

            // add NamespaceUri 
            var NamespaceUriAttr = new AttributeFamilyType(new System.Xml.Linq.XElement(defaultNS + "AttributeType"));
            att = NamespaceUriAttr as AttributeTypeType;
            NamespaceUriAttr.Name = "NamespaceUri";
            att.AttributeDataType = "xs:anyURI";
            atl_meta.AttributeType.Insert(NamespaceUriAttr, false);

            // add ExplicitNodeId
            var ExplicitNodeIdAttr = new AttributeFamilyType(new System.Xml.Linq.XElement(defaultNS + "AttributeType"));
            att = ExplicitNodeIdAttr as AttributeTypeType;
            ExplicitNodeIdAttr.Name = "ExplicitNodeId";
            
            added2 = new AttributeType(new System.Xml.Linq.XElement(defaultNS + "Attribute"));
            added2.Name = "NamespaceUri";
            added2.RecreateAttributeInstance(NamespaceUriAttr);
            ExplicitNodeIdAttr.Insert(added2, false);

            added2 = new AttributeType(new System.Xml.Linq.XElement(defaultNS + "Attribute"));
            added2.Name= "NumericId";
            added2.AttributeDataType = "xs:long";
            ExplicitNodeIdAttr.Insert(added2, false);

            added2 = new AttributeType(new System.Xml.Linq.XElement(defaultNS + "Attribute"));
            added2.Name = "StringId";
            added2.AttributeDataType = "xs:string";
            ExplicitNodeIdAttr.Insert(added2, false);

            added2 = new AttributeType(new System.Xml.Linq.XElement(defaultNS + "Attribute"));
            added2.Name = "GuidId";
            added2.AttributeDataType = "xs:string";
            ExplicitNodeIdAttr.Insert(added2, false);

            added2 = new AttributeType(new System.Xml.Linq.XElement(defaultNS + "Attribute"));
            added2.Name = "OpaqueId";
            added2.AttributeDataType = "xs:base64Binary";
            ExplicitNodeIdAttr.Insert(added2, false);

            atl_meta.AttributeType.Insert(ExplicitNodeIdAttr, false);

            // add Alias
            var AliasAttr = new AttributeFamilyType(new System.Xml.Linq.XElement(defaultNS + "AttributeType"));
            att = AliasAttr as AttributeTypeType;
            AliasAttr.Name = "Alias";
            
            added2 = new AttributeType(new System.Xml.Linq.XElement(defaultNS + "Attribute"));
            added2.Name = "AliasName";
            added2.AttributeDataType = "xs:string";
            AliasAttr.Insert(added2, false);

            added2 = new AttributeType(new System.Xml.Linq.XElement(defaultNS + "Attribute"));
            added2.Name = "ReferenceTypeFilter";
            added2.RecreateAttributeInstance(ExplicitNodeIdAttr);
            AliasAttr.Insert(added2, false);

            atl_meta.AttributeType.Insert(AliasAttr, false);

            // add UABaseRole to the RCL
            var rcl_meta = m_cAEXDocument.CAEXFile.RoleClassLib.Append(RCLPrefix + MetaModelName);
            var br = rcl_meta.New_RoleClass(UaBaseRole);
            br.RefBaseClassPath = "AutomationMLBaseRoleClassLib/AutomationMLBaseRole";
            AddLibraryHeaderInfo(rcl_meta as CAEXBasicObject);

            // add meta model SUC
            var suc_meta = m_cAEXDocument.CAEXFile.SystemUnitClassLib.Append(SUCPrefix + MetaModelName);
            // add MethodNodeClass to the SUC
            // This will add a guid ID, as UaMethodNodeClass is not in the Nodeset file
            var methodNodeClass = suc_meta.New_SystemUnitClass( MethodNodeClass );
            // Give this a known repeatable ID, as there is no node ID for it.
            string methodNodeClassUniqueId = "686619c7-0101-4869-b398-aa0f98bc5f54";
            methodNodeClass.ID = methodNodeClassUniqueId;
            methodNodeClass.New_SupportedRoleClass( RCLPrefix + MetaModelName + "/" + UaBaseRole, false );
            // Issue 16 Add BrowseName to SUC UaMethodNodeClass
            // Manually add these to simulate what the AddModifyAttribute would do if it were possible
            AttributeType browseNameAttributeType = methodNodeClass.Attribute.Append( "BrowseName" );
            browseNameAttributeType.RefAttributeType = "[" + ATLPrefix + Opc.Ua.Namespaces.OpcUa + "]/[QualifiedName]";
            AttributeType namespaceUriAttributeType = browseNameAttributeType.Attribute.Append( "NamespaceUri" );
            namespaceUriAttributeType.AttributeDataType = "xs:anyURI";
            AttributeType nameAttributeType = browseNameAttributeType.Attribute.Append( "Name" );
            nameAttributeType.AttributeDataType = "xs:string";

            AddLibraryHeaderInfo( suc_meta as CAEXBasicObject);
        }

        private void AddBaseNodeClassAttributes( AttributeSequence seq, UANode uanode)
        {
            var nodeId = seq["NodeId"];

            if (uanode.DecodedNodeId.IsNullNodeId == false)
            {
                ExpandedNodeId expandedNodeId = new ExpandedNodeId(uanode.DecodedNodeId,
                    m_modelManager.ModelNamespaceIndexes[uanode.DecodedNodeId.NamespaceIndex].NamespaceUri);
                Variant variant = new Variant(expandedNodeId);
                nodeId = AddModifyAttribute(seq, "NodeId", "NodeId", variant);
            }

            AttributeType browse = seq["BrowseName"];
            if (browse == null)
            {
                browse = AddModifyAttribute(seq, "BrowseName", "QualifiedName", Variant.Null);
            }

            // Ensure that NamespaceUri is always present #100
            AttributeType uriAttribute = browse.Attribute["NamespaceUri"];
            uriAttribute.Value = 
                m_modelManager.ModelNamespaceIndexes[uanode.DecodedBrowseName.NamespaceIndex].NamespaceUri;

            // Remove the name for everything #100
            AttributeType nameSubAttribute = browse.Attribute["Name"];
            if (nameSubAttribute != null)
            {
                browse.Attribute.RemoveElement(nameSubAttribute);
            }

            BuildLocalizedTextAttribute( seq, "DisplayName", uanode.DisplayName, 
                uanode.DecodedBrowseName.Name, ignoreEqual: true );
            BuildLocalizedTextAttribute( seq, "Description", uanode.Description,
                uanode.DecodedBrowseName.Name, ignoreEqual: false );

            UAType uaType = uanode as UAType;
            if (  uaType != null && uaType.IsAbstract )
            {
                AddModifyAttribute(seq, "IsAbstract", "Boolean", uaType.IsAbstract);
            }

            UAVariableType uaVariableType = uanode as UAVariableType;
            if (uaVariableType != null)
            {
                if (uaVariableType.ValueRank != -1)
                {
                    AddModifyAttribute(seq, "ValueRank", "Int32", uaVariableType.ValueRank);
                }
            }
        }

        private void BuildLocalizedTextAttribute( 
            AttributeSequence seq, 
            string attributeName,
            NodeSet.LocalizedText[] localizedTexts,
            string equalityString, bool ignoreEqual )
        {
            if( localizedTexts != null )
            {
                CAEXObject findObject = m_cAEXDocument.FindByPath( 
                    "AutomationMLBaseAttributeTypeLib/LocalizedAttribute" );
                AttributeFamilyType localizedAttributeFamilyType = findObject as AttributeFamilyType;

                if( localizedTexts.Length > 1 )
                {
                    AttributeType root = AddModifyAttribute( seq, attributeName, 
                        "LocalizedText", localizedTexts[ 0 ].Value ); ;
                    if( root != null )
                    {
                        string previousLocaleId = string.Empty;
                        for( int index = 0; index < localizedTexts.Length; index++ )
                        {
                            NodeSet.LocalizedText localizedText = localizedTexts[ index ];
                            string localeId = GetLocaleId( localizedText, ref previousLocaleId );
                            AttributeType textAttribute = root.Attribute.Append( localeId );
                            textAttribute.RecreateAttributeInstance( localizedAttributeFamilyType );
                            textAttribute.Value = localizedText.Value;
                        }
                    }
                }
                else if( localizedTexts.Length > 0 )
                {
                    NodeSet.LocalizedText localizedText = localizedTexts[ 0 ];
                    if( ignoreEqual == false ||
                        localizedText.Value != equalityString )
                    {
                        AttributeType root = AddModifyAttribute( seq, attributeName, "LocalizedText", localizedText.Value );

                        if( !String.IsNullOrEmpty( localizedText.Locale ) )
                        {
                            AttributeType textAttribute = root.Attribute.Append( localizedText.Locale );
                            textAttribute.RecreateAttributeInstance( localizedAttributeFamilyType );
                            textAttribute.Value = localizedText.Value;
                        }
                    }
                }
            }
        }

        private string GetLocaleId( NodeSet.LocalizedText localizedText, ref string lastUnknownLocale )
        {
            string localeId = string.Empty;

            if ( localizedText != null )
            {
                if ( String.IsNullOrEmpty( localizedText.Locale ) )
                {
                    if ( String.IsNullOrEmpty( lastUnknownLocale ) )
                    {
                        localeId = "qaa";
                    }
                    else
                    {
                        if ( lastUnknownLocale.Length == 3 && lastUnknownLocale[0] == 'q' )
                        {
                            char secondChar  = lastUnknownLocale[1];
                            char lastChar = lastUnknownLocale[ 2 ];
                            if ( lastChar == 'z' )
                            {
                                // It's pretty impractical to have 20*26 unknown locales for a single node.
                                if( secondChar < 't' )
                                {
                                    secondChar++;
                                    localeId = "q" + secondChar + 'a';
                                }
                            }
                            else
                            {
                                localeId = "q" + secondChar + (char)( lastChar + 1 );
                            }
                        }
                    }
                    lastUnknownLocale = localeId;
                }
                else
                {
                    localeId = localizedText.Locale;
                }
                
                return localeId;
            }

            return localeId;
        }

        private AttributeType AddModifyAttribute(AttributeSequence seq, string name, string refDataType, Variant val, bool bListOf = false, string sURI = uaNamespaceURI)
        {
            string sUADataType = refDataType;
            string ListOfPrefix = "";
            if (bListOf == true)
                ListOfPrefix = ListOf;
            string path = BuildLibraryReference(ATLPrefix, sURI, ListOfPrefix + sUADataType);
            var ob = m_cAEXDocument.FindByPath(path);
            var sourceAttribute = ob as AttributeFamilyType;
            AttributeType desiredAttribute = seq[name];  //find the existing attribute with the name
            if (desiredAttribute == null)
            {
                if (bListOf == false && val.TypeInfo != null)  // look for reasons not to add the attribute because missing == default value
                {
                    if (name == "IsSource" && val == false)
                        return null;
                    if (name == "Symmetric" && val == false)
                        return null;
                }
                desiredAttribute = seq.Append(name);  // not found so create a new one
            }

            RecreateAttributeInstance(sourceAttribute, desiredAttribute);

            if (val.TypeInfo != null)
            {
                if (bListOf == true)
                {
                    string nodeName = "";
                    string referenceName = "";
                    CAEXWrapper reference = seq.CAEXOwner;
                    if (reference != null)
                    {
                        referenceName = reference.Name();
                        CAEXWrapper referenceParent = reference.CAEXParent;
                        if (referenceParent != null)
                        {
                            nodeName = referenceParent.Name();
                        }
                    }

                    bool addElements = true;

                    switch( val.TypeInfo.BuiltInType )
                    {
                        case BuiltInType.LocalizedText:
                            {
                                if( refDataType == "LocalizedText" && referenceName == "EnumStrings" )
                                {
                                    addElements = false;
                                    AttributeTypeType attributeType = desiredAttribute as AttributeTypeType;
                                    if( attributeType != null )
                                    {
                                        AttributeValueRequirementType stringValueRequirement = new AttributeValueRequirementType(
                                            new System.Xml.Linq.XElement( defaultNS + "Constraint" ) );
                                        stringValueRequirement.Name = nodeName + " Constraint";
                                        attributeType.AttributeDataType = "xs:string";
                                        NominalScaledTypeType stringValueNominalType = stringValueRequirement.New_NominalType();

                                        Opc.Ua.LocalizedText[] values = val.Value as Opc.Ua.LocalizedText[];
                                        if( values != null )
                                        {
                                            foreach( Opc.Ua.LocalizedText value in values )
                                            {
                                                stringValueNominalType.RequiredValue.Append( value.Text );
                                            }
                                            attributeType.Constraint.Insert( stringValueRequirement );
                                        }
                                    }
                                }

                                break;
                            }

                        case BuiltInType.ExtensionObject:
                            {
                                addElements = false;

                                if ( refDataType == "EnumValueType" && referenceName == "EnumValues" )
                                {
                                    AttributeTypeType attributeType = desiredAttribute as AttributeTypeType;
                                    if( attributeType != null )
                                    {
                                        AttributeValueRequirementType stringValueRequirement = new AttributeValueRequirementType(
                                            new System.Xml.Linq.XElement( defaultNS + "Constraint" ) );
                                        stringValueRequirement.Name = nodeName + " Constraint";
                                        attributeType.AttributeDataType = "xs:string";
                                        NominalScaledTypeType stringValueNominalType = stringValueRequirement.New_NominalType();

                                        ExtensionObject[] values = val.Value as ExtensionObject[];
                                        if( values != null )
                                        {
                                            foreach( ExtensionObject value in values )
                                            {
                                                EnumValueType enumValueType = value.Body as EnumValueType;
                                                if( enumValueType != null )
                                                {
                                                    stringValueNominalType.RequiredValue.Append( enumValueType.DisplayName.Text );
                                                }
                                            }
                                            attributeType.Constraint.Insert( stringValueRequirement );
                                        }
                                    }
                                }
                                else
                                {
                                    IList valueAsList = val.Value as IList;
                                    if (valueAsList != null)
                                    {
                                        for (int index = 0; index < valueAsList.Count; index++)
                                        {
                                            Variant elementVariant = new Variant(valueAsList[index]);
                                            ExtensionObject extensionObject = elementVariant.Value as ExtensionObject;
                                            NodeId typeId = ExpandedNodeId.ToNodeId(extensionObject.TypeId, m_modelManager.NamespaceUris);

                                            Type extentionObjectType = extensionObject.Body.GetType();
                                            if ( extensionObject.Body.GetType().FullName.Equals( "System.Xml.XmlElement") )
                                            {
                                                XmlElement xmlElement = extensionObject.Body as XmlElement;
                                                if (xmlElement != null)
                                                {
                                                    UANode potential = GetDataTypeFromXmlElement(xmlElement, typeId);
                                                    if ( potential != null )
                                                    {
                                                        typeId = potential.DecodedNodeId;
                                                    }
                                                }
                                            }

                                            bool elementListOf = elementVariant.TypeInfo.ValueRank >= ValueRanks.OneDimension;

                                            AddModifyAttribute(desiredAttribute.Attribute, index.ToString(), typeId, elementVariant, elementListOf);
                                        }
                                    }
                                }

                                break;
                            }
                    }

                    if( addElements )
                    {
                        IList valueAsList = val.Value as IList;
                        if( valueAsList != null )
                        {
                            for( int index = 0; index < valueAsList.Count; index++ )
                            {
                                Variant elementVariant = new Variant( valueAsList[ index ] );
                                bool elementListOf = elementVariant.TypeInfo.ValueRank >= ValueRanks.OneDimension;
                                AddModifyAttribute( desiredAttribute.Attribute, index.ToString(), refDataType, 
                                    elementVariant, elementListOf, sURI );
                            }
                        }
                    }
                }
                else
                {
                    bool handled = true;
                    switch (val.TypeInfo.BuiltInType)  // TODO -- consider supporting setting values for more complicated types (enums, structures, Qualified Names ...) and arrays
                    {
                        case BuiltInType.Boolean:
                        case BuiltInType.SByte:
                        case BuiltInType.Byte:
                        case BuiltInType.Int16:
                        case BuiltInType.UInt16:
                        case BuiltInType.Int32:
                        case BuiltInType.UInt32:
                        case BuiltInType.Int64:
                        case BuiltInType.UInt64:
                        case BuiltInType.Float:
                        case BuiltInType.Double:
                        case BuiltInType.String:
                        case BuiltInType.DateTime:
                        case BuiltInType.Guid:
                        case BuiltInType.XmlElement:
                        case BuiltInType.Number:
                        case BuiltInType.Integer:
                        case BuiltInType.UInteger:
                        case BuiltInType.Enumeration:
                            {
                                desiredAttribute.AttributeValue = val;
                                break;
                            }

                        case BuiltInType.ByteString:
                            {
                                byte[] bytes = val.Value as byte[];
                                if ( bytes != null )
                                {
                                    string encoded = Convert.ToBase64String( bytes, 0, bytes.Length );
                                    desiredAttribute.AttributeValue = new Variant( encoded.ToString() );
                                }

                                break;
                            }

                        case BuiltInType.NodeId:
                        case BuiltInType.ExpandedNodeId:
                            {
                                NodeId nodeId = null;
                                ExpandedNodeId expandedNodeId = null;
                                if( val.TypeInfo.BuiltInType == BuiltInType.NodeId )
                                {
                                    nodeId = val.Value as NodeId;
                                }
                                else
                                {
                                    expandedNodeId = val.Value as ExpandedNodeId;
                                    nodeId = ExpandedNodeId.ToNodeId( expandedNodeId, m_modelManager.NamespaceUris);
                                }

                                if ( nodeId != null )
                                {
                                    desiredAttribute.AttributeValue = nodeId;
                                    AttributeType rootNodeId = desiredAttribute.Attribute[ "RootNodeId" ];
                                    if ( rootNodeId != null )
                                    {
                                        AttributeType namespaceUri = rootNodeId.Attribute[ "NamespaceUri" ];
                                        if ( namespaceUri != null )
                                        {
                                            namespaceUri.Value = m_modelManager.ModelNamespaceIndexes[ nodeId.NamespaceIndex ].NamespaceUri;
                                        }

                                        switch( nodeId.IdType )
                                        {
                                            case IdType.Numeric:
                                                {
                                                    AttributeType id = rootNodeId.Attribute[ "NumericId" ];
                                                    id.Value = nodeId.Identifier.ToString();
                                                    break;
                                                }

                                            case IdType.String:
                                                {
                                                    AttributeType id = rootNodeId.Attribute[ "StringId" ];
                                                    id.Value = nodeId.Identifier.ToString();
                                                    break;
                                                }

                                            case IdType.Guid:
                                                {
                                                    AttributeType id = rootNodeId.Attribute[ "GuidId" ];
                                                    id.Value = nodeId.Identifier.ToString();
                                                    break;
                                                }

                                            case IdType.Opaque:
                                                {
                                                    byte[] bytes = nodeId.Identifier as byte[];
                                                    if( bytes != null )
                                                    {
                                                        AttributeType id = rootNodeId.Attribute[ "OpaqueId" ];
                                                        string encoded = Convert.ToBase64String(
                                                            bytes, 0, bytes.Length );
                                                        id.Value = encoded;
                                                    }
                                                    break;
                                                }
                                        }
                                    }
                                    desiredAttribute.DefaultValue = null;
                                    desiredAttribute.Value = null;

                                    MinimizeNodeId( desiredAttribute );
                                }

                                if ( expandedNodeId != null )
                                {
                                    if ( expandedNodeId.ServerIndex > 0 )
                                    {
                                        if( expandedNodeId.ServerIndex < m_modelManager.ModelNamespaceIndexes.Count )
                                        {
                                            string serverUri = m_modelManager.ModelNamespaceIndexes[ (int)expandedNodeId.ServerIndex ].NamespaceUri;
                                            AttributeType serverInstanceUri = desiredAttribute.Attribute[ "ServerInstanceUri" ];
                                            if ( serverInstanceUri != null )
                                            {
                                                serverInstanceUri.Value = serverUri;
                                            }
                                        }
                                    }
                                }

                                break;
                            }

                        case BuiltInType.StatusCode:
                            {
                                StatusCode statusCode = (StatusCode)val.Value;
                                desiredAttribute.AttributeValue = statusCode.Code;

                                break;
                            }

                        case BuiltInType.QualifiedName:
                            {
                                desiredAttribute.AttributeValue = val;

                                QualifiedName qualifiedName = val.Value as QualifiedName;
                                if( qualifiedName != null )
                                {
                                    AttributeType uri = desiredAttribute.Attribute[ "NamespaceUri" ];
                                    uri.Value = m_modelManager.ModelNamespaceIndexes[ qualifiedName.NamespaceIndex ].NamespaceUri;
                                    AttributeType nameAttribute = desiredAttribute.Attribute[ "Name" ];
                                    nameAttribute.Value = qualifiedName.Name;
                                    desiredAttribute.DefaultValue = null;
                                    desiredAttribute.Value = null;
                                }

                                break;
                            }

                        case BuiltInType.LocalizedText:
                            {
                                Opc.Ua.LocalizedText localizedText = (Opc.Ua.LocalizedText)val.Value;
                                if( localizedText != null && localizedText.Text != null )
                                {
                                    desiredAttribute.AttributeValue = localizedText.Text;
                                    if ( !string.IsNullOrEmpty( localizedText.Locale ) )
                                    {
                                        CAEXObject findObject = m_cAEXDocument.FindByPath(
                                            "AutomationMLBaseAttributeTypeLib/LocalizedAttribute" );
                                        AttributeFamilyType localizedAttributeFamilyType = 
                                            findObject as AttributeFamilyType;
                                        AttributeType textAttribute = desiredAttribute.Attribute.Append( localizedText.Locale );
                                        textAttribute.RecreateAttributeInstance( localizedAttributeFamilyType );
                                        textAttribute.Value = localizedText.Text;
                                    }
                                }

                                break;
                            }

                        case BuiltInType.ExtensionObject:
                            {
                                ExtensionObject extensionObject = val.Value as ExtensionObject;
                                if ( extensionObject != null && extensionObject.Body != null )
                                {
                                    AddModifyExtensionObject( desiredAttribute, extensionObject );
                                }

                                break;
                            }

                        case BuiltInType.DataValue:
                            {
                                DataValue dataValue = val.Value as DataValue;
                                if( dataValue != null )
                                {
                                    Variant actualValue = new Variant( dataValue.Value );
                                    NodeId dataTypeNodeId = Opc.Ua.TypeInfo.GetDataTypeId( actualValue.Value );

                                    bool actualListOf = actualValue.TypeInfo.ValueRank >= ValueRanks.OneDimension;

                                    AddModifyAttribute( desiredAttribute.Attribute, "Value", dataTypeNodeId, actualValue, actualListOf );
                                    AddModifyAttribute( desiredAttribute.Attribute, "StatusCode", "StatusCode",
                                        dataValue.StatusCode );
                                    AddModifyAttribute( desiredAttribute.Attribute, "SourceTimestamp", "DateTime",
                                        dataValue.SourceTimestamp );
                                    if( dataValue.SourcePicoseconds > 0 )
                                    {
                                        AddModifyAttribute( desiredAttribute.Attribute, "SourcePicoseconds", "UInt16",
                                            dataValue.SourcePicoseconds );
                                    }
                                    AddModifyAttribute( desiredAttribute.Attribute, "ServerTimestamp", "DateTime",
                                        dataValue.ServerTimestamp );
                                    if ( dataValue.ServerPicoseconds > 0 )
                                    {
                                        AddModifyAttribute( desiredAttribute.Attribute, "ServerPicoseconds", "UInt16",
                                            dataValue.ServerPicoseconds );
                                    }
                                }
                                break;
                            }

                        case BuiltInType.Variant:
                            {
                                Variant internalVariant = new Variant( val.Value );
                                NodeId dataTypeNodeId = Opc.Ua.TypeInfo.GetDataTypeId( internalVariant.Value );

                                bool internalListOf = internalVariant.TypeInfo.ValueRank >= ValueRanks.OneDimension;

                                AddModifyAttribute( desiredAttribute.Attribute, "Value", dataTypeNodeId, internalVariant, internalListOf );

                                break;
                            }

                        case BuiltInType.DiagnosticInfo:
                            {
                                AddModifyAttributeObject( desiredAttribute, val.Value );
                                break;
                            }

                        default:
                            handled = false;
                            break;
                        
                    }

                    if ( handled && refDataType.Equals( "BaseDataType" ) )
                    {
                        // this is specifically the variant case
                        NodeId dataTypeFromBase = new NodeId( (uint)val.TypeInfo.BuiltInType );
                        string variantDataType = GetAttributeDataType( dataTypeFromBase );
                        desiredAttribute.AttributeDataType = variantDataType;
                    }
                }
            }

            return desiredAttribute;
        }

        private bool MinimizeNodeId( AttributeType nodeIdAttribute )
        {
            bool minimized = false;

            AttributeType rootNodeId = nodeIdAttribute.Attribute[ "RootNodeId" ];

            if ( rootNodeId != null )
            {
                if ( MinimizeExplicitNodeId( rootNodeId ) )
                {
                    nodeIdAttribute.Attribute.Remove();
                    nodeIdAttribute.Attribute.Insert( rootNodeId );
                    minimized = true;
                }
            }

            return minimized;
        }

        private bool MinimizeExplicitNodeId( AttributeType explicitNodeIdAttribute )
        {
            bool minimized = false;

            if( explicitNodeIdAttribute != null )
            {
                string path = ATLPrefix + MetaModelName + "/ExplicitNodeId";
                if( explicitNodeIdAttribute.RefAttributeType.Equals( path ) )
                {
                    // If NamespaceUri is empty, don't do anything, for nothing is set, and cannot minimize
                    AttributeType namespaceUri = explicitNodeIdAttribute.Attribute[ "NamespaceUri" ];
                    if( namespaceUri != null && namespaceUri.Value.Length > 0 )
                    {
                        string keepAttributeName = string.Empty;
                        foreach( string idAttributeName in NodeId_IdAttributeNames )
                        {
                            AttributeType nodeIdTypeAttribute = explicitNodeIdAttribute.Attribute[ idAttributeName ];
                            if( nodeIdTypeAttribute != null &&
                                nodeIdTypeAttribute.Value != null &&
                                nodeIdTypeAttribute.Value.Length > 0 )
                            {
                                keepAttributeName = idAttributeName;
                                minimized = true;
                                break;
                            }
                        }

                        if( keepAttributeName != string.Empty )
                        {
                            foreach( string idAttributeName in NodeId_IdAttributeNames )
                            {
                                if( !idAttributeName.Equals( keepAttributeName ) )
                                {
                                    AttributeType nodeIdTypeAttribute = explicitNodeIdAttribute.Attribute[ idAttributeName ];
                                    if( nodeIdTypeAttribute != null )
                                    {
                                        explicitNodeIdAttribute.Attribute.RemoveElement( nodeIdTypeAttribute );
                                    }
                                }
                            }
                        }
                    }
                }
            }

            return minimized;
        }

        private UANode GetDataTypeFromXmlElement( XmlElement xmlElement, NodeId defined )
        {
            UANode dataType = m_modelManager.FindNode<UANode>(defined);
            UAObject uaObject = dataType as UAObject;
            if (uaObject != null)
            {
                // Look for encoding
                List<ReferenceInfo> referenceList = m_modelManager.FindReferences(defined);
                foreach (ReferenceInfo referenceInfo in referenceList)
                {
                    if (!referenceInfo.IsForward && referenceInfo.ReferenceTypeId.Equals(
                        Opc.Ua.ReferenceTypeIds.HasEncoding))
                    {
                        UANode potential = m_modelManager.FindNode<UANode>(referenceInfo.TargetId);
                        if ( potential != null )
                        {
                            dataType = potential;
                        }
                        break;
                    }
                }
            }

            UADataType uaDataType = dataType as UADataType;
            if (uaDataType != null)
            {
                if (uaDataType.IsAbstract)
                {
                    List<ReferenceInfo> dataTypeReferences = m_modelManager.FindReferences(uaDataType.DecodedNodeId);
                    Dictionary<NodeId, UANode> dataTypeDictionary = new Dictionary<NodeId, UANode>();
                    foreach( ReferenceInfo referenceInfo in dataTypeReferences)
                    {
                        if (referenceInfo.IsForward && referenceInfo.ReferenceTypeId.Equals(
                            Opc.Ua.ReferenceTypeIds.HasSubtype))
                        {
                            UANode dataTypeUaNode = m_modelManager.FindNode<UANode>(referenceInfo.TargetId);
                            if (dataTypeUaNode != null)
                            {
                                dataTypeDictionary.Add(referenceInfo.TargetId, dataTypeUaNode);
                            }
                        }
                    }

                    // Go find the actual typedefinition
                    XmlElement type = SearchForElement("TypeId", xmlElement);
                    if (type != null)
                    {
                        XmlElement typeIdentifier = SearchForElement("Identifier", type);
                        if (typeIdentifier != null)
                        {
                            // This is not accurate, I'm just lucky at this point
                            // I'm not lucky anymore.
                            NodeId nodeId = new NodeId(typeIdentifier.InnerText);
                            if (nodeId != null && !nodeId.IsNullNodeId)
                            {
                                UANode potential = GetDataTypeFromXmlElement(xmlElement, nodeId);
                                if (dataTypeDictionary.ContainsKey(potential.DecodedNodeId))
                                {
                                    dataType = potential;
                                }
                                else
                                {
                                    UADataType potentialDataType = potential as UADataType;
                                    if (potentialDataType != null && potentialDataType.IsAbstract == false)
                                    {
                                        dataType = potential;
                                    }
                                }
                            }
                        }
                    }
                }
            }

            return dataType;
        }

        private Dictionary<string, DataTypeField> CreateFieldReferenceTypes( AttributeType attribute, NodeId typeNodeId )
        {
            string typeNodeIdString = typeNodeId.ToString();

            if( !ReferenceAttributeMap.ContainsKey( typeNodeIdString ) )
            {
                UANode typeUaNode = m_modelManager.FindNode<UANode>( typeNodeId );

                if( typeUaNode != null )
                {
                    NodeSet.UADataType typeDataType = typeUaNode as NodeSet.UADataType;
                    if( typeDataType != null && typeDataType.Definition != null && typeDataType.Definition.Field != null )
                    {
                        Dictionary<string, DataTypeField> fields = new Dictionary<string, DataTypeField>();

                        foreach( DataTypeField dataTypeField in typeDataType.Definition.Field )
                        {
                            fields.Add( dataTypeField.Name, dataTypeField );
                        }

                        // Recurse base object types
                        NodeId baseNodeId = m_modelManager.FindFirstTarget( typeNodeId, HasSubTypeNodeId, false );
                        if( baseNodeId != null )
                        {
                            Dictionary<string, DataTypeField> baseFields = CreateFieldReferenceTypes( attribute, baseNodeId );

                            if ( baseFields != null )
                            {
                                foreach( KeyValuePair<string, DataTypeField> pair in baseFields )
                                {
                                    if( !fields.ContainsKey( pair.Key ) )
                                    {
                                        fields.Add( pair.Key, pair.Value );
                                    }
                                }
                            }
                        }

                        ReferenceAttributeMap.Add( typeNodeIdString, fields );
                    }
                }
            }

            Dictionary<string, DataTypeField> attributeMap = new Dictionary<string, DataTypeField>();
            ReferenceAttributeMap.TryGetValue( typeNodeIdString, out attributeMap );
            return attributeMap;
        }

        private DataTypeField GetFieldReferenceType( string type, string field )
        {
            DataTypeField dataTypeField = null;

            Dictionary<string, DataTypeField> typeMap;

            if( ReferenceAttributeMap.TryGetValue( type, out typeMap ) )
            {
                string lookFor = field;

                string findMe = field;
                if( field.StartsWith( "ListOf" ) )
                {
                    findMe = field.Replace( "ListOf", "" );
                }

                typeMap.TryGetValue( findMe, out dataTypeField );
            }

            return dataTypeField;
        }

        private void AddModifyAttributeObject( AttributeType attribute, object value )
        {
            Type valueType = value.GetType();

            if( valueType.FullName.StartsWith( "Opc.Ua." ) )
            {
                PropertyInfo[] properties = value.GetType().GetProperties();

                foreach( PropertyInfo property in properties )
                {
                    NodeId propertyNodeId = Opc.Ua.TypeInfo.GetDataTypeId( property.PropertyType );
                    var propertyValue = property.GetValue( value );
                    if( propertyValue != null )
                    {
                        AddModifyAttribute( attribute.Attribute,
                            property.Name, propertyNodeId,
                            new Variant( propertyValue ),
                            property.PropertyType.IsArray );
                    }
                }
            }
        }

        private void AddModifyExtensionObject( AttributeType attribute, ExtensionObject extensionObject )
        {
            NodeId typeNodeId = ExpandedNodeId.ToNodeId( extensionObject.TypeId, m_modelManager.NamespaceUris );
            string typeNodeIdString = typeNodeId.ToString();

            object value = extensionObject.Body;

            Type valueType = value.GetType();

            if( valueType.FullName.StartsWith( "Opc.Ua." ) )
            {
                Dictionary<string, DataTypeField> fieldReferenceTypes = CreateFieldReferenceTypes( 
                    attribute, typeNodeId );

                PropertyInfo[] properties = value.GetType().GetProperties();

                HashSet<string> exclusions = GetExclusions( true );

                foreach( PropertyInfo property in properties )
                {
                    DataTypeField fieldDefinitionNode = GetFieldReferenceType(
                        typeNodeIdString, property.Name );

                    if ( fieldDefinitionNode != null )
                    {
                        NodeId fieldDefinitionNodeId = fieldDefinitionNode.DecodedDataType;

                        if( fieldDefinitionNodeId != null )
                        {
                            var propertyValue = property.GetValue( value );
                            if( propertyValue != null )
                            {
                                Variant fieldVariant = new Variant( propertyValue );
                                if( fieldVariant.TypeInfo.BuiltInType == BuiltInType.ExtensionObject )
                                {
                                    ExtensionObject[] arrayValues = fieldVariant.Value as ExtensionObject[];
                                    if( arrayValues != null )
                                    {
                                        foreach( ExtensionObject arrayValue in arrayValues )
                                        {
                                            arrayValue.TypeId = new ExpandedNodeId( fieldDefinitionNodeId );
                                        }
                                    }
                                    else
                                    {
                                        ExtensionObject notAnArray = fieldVariant.Value as ExtensionObject;
                                        if( notAnArray != null )
                                        {
                                            notAnArray.TypeId = new ExpandedNodeId( fieldDefinitionNodeId );
                                        }
                                    }
                                }

                                if( !exclusions.Contains( property.Name ) )
                                {
                                    bool isList = fieldVariant.TypeInfo.ValueRank > ValueRanks.Scalar;

                                    AddModifyAttribute( attribute.Attribute,
                                        property.Name, fieldDefinitionNodeId, fieldVariant,
                                        isList );
                                }
                            }
                        }

                    }
                }
            }
            else
            {
                XmlElement xmlElement = value as XmlElement;
                if( xmlElement != null )
                {
                    UANode typeDefinition = GetDataTypeFromXmlElement( xmlElement, typeNodeId);

                    Dictionary<string, DataTypeField> fieldReferenceTypes = CreateFieldReferenceTypes(
                        attribute, typeDefinition.DecodedNodeId);

                    if ( fieldReferenceTypes != null )
                    {
                        foreach( KeyValuePair<string, DataTypeField> fieldReferenceType in fieldReferenceTypes )
                        {
                            UANode fieldTypeDefinition = GetDataTypeFromXmlElement(xmlElement, 
                                fieldReferenceType.Value.DecodedDataType );

                            Variant fieldVariant = CreateComplexVariant( fieldReferenceType.Key, 
                                fieldReferenceType.Value, xmlElement );

                            string attributeName = fieldReferenceType.Key;
                            NodeId createDataType = fieldReferenceType.Value.DecodedDataType;

                            bool listOf = fieldReferenceType.Value.ValueRank >= ValueRanks.OneDimension;

                            AddModifyAttribute(
                                attribute.Attribute,
                                attributeName,
                                createDataType,
                                fieldVariant, 
                                bListOf: listOf);
                        }
                    }
                }
            }
        }

        private HashSet<string> GetExclusions( bool extensionObject )
        {
            if ( ExtensionObjectExclusions == null )
            {
                ExtensionObjectExclusions = new HashSet<string>();
                ExtensionObjectExclusions.Add( "BinaryEncodingId" );
                ExtensionObjectExclusions.Add( "JsonEncodingId" );
                ExtensionObjectExclusions.Add( "XmlEncodingId" );
                ExtensionObjectExclusions.Add( "TypeId" );
            }
            return ExtensionObjectExclusions;
        }

        private void UpdateDerived(ref InternalElementType internalElement, NodeId utilized, NodeId actual)
        { 
            // This should theoretically do all of it.  and run it through a consistent path.
            if (!utilized.Equals(actual))
            {
                List<string> referenceNames = new List<string>();

                List<ReferenceInfo> referenceList = m_modelManager.FindReferences(actual);

                if (referenceList != null)
                {
                    foreach (ReferenceInfo referenceInfo in referenceList)
                    {
                        if (referenceInfo.IsForward)
                        {
                            UANode foundNodeId = FindNode<UANode>(referenceInfo.TargetId);
                            if (foundNodeId.NodeClass == NodeClass.Variable)
                            {
                                UAVariable foundVariable = foundNodeId as UAVariable;
                                if (foundVariable != null)
                                {
                                    referenceNames.Add(foundVariable.BrowseName);
                                }
                            }
                        }
                    }
                }

                foreach (string referenceName in referenceNames)
                {
                    InternalElementType referenceElement = internalElement.InternalElement[referenceName];

                    if (referenceElement != null)
                    {
                        AttributeType valueAttribute = referenceElement.Attribute["Value"];
                        if (valueAttribute != null)
                        {
                            NodeId propertyId = m_modelManager.FindFirstTarget(actual, HasPropertyNodeId, true, referenceName);
                            if (propertyId != null)
                            {
                                var propertyNode = FindNode<UANode>(propertyId);
                                var propertyNodeValue = propertyNode as UAVariable;

                                if ( propertyNodeValue != null)
                                {
                                    bool bListOf = (propertyNodeValue.ValueRank >= ValueRanks.OneDimension );

                                    AddModifyAttribute( referenceElement.Attribute, 
                                        "Value",
                                        propertyNodeValue.DecodedDataType, 
                                        propertyNodeValue.DecodedValue, 
                                        bListOf );
                                }
                            }
                        }
                    }
                }
            }
        }

        private AttributeType AddModifyAttribute(AttributeSequence seq, string name, NodeId refDataType, Variant val, bool bListOf = false)
        {
            var dataTypeNode = FindNode<UANode>(refDataType);
            var sUADataType = dataTypeNode.DecodedBrowseName.Name;
            var sURI = m_modelManager.FindModelUri(dataTypeNode.DecodedNodeId);

            AttributeType returnAttributeType = null;

            if( dataTypeNode.DecodedNodeId.Equals( Opc.Ua.DataTypeIds.Byte ) &&
                name.Equals( "BuiltInType", StringComparison.OrdinalIgnoreCase ) )
            {
                // Special Case, Part 83 A.3.6
                returnAttributeType = AddModifyAttributeBuiltInType( seq, refDataType, val ); 
            }
            else if( m_modelManager.IsTypeOf( dataTypeNode.DecodedNodeId, EnumerationNodeId ) == true )
            {
                returnAttributeType = AddModifyAttributeEnum( seq, name, refDataType, val );
            }

            if ( returnAttributeType == null )
            {
                returnAttributeType = AddModifyAttribute( seq, name, sUADataType, val, bListOf, sURI );
            }

            return returnAttributeType;
        }

        private AttributeType AddModifyAttributeBuiltInType( AttributeSequence seq,
            NodeId refDataType,
            Variant val )
        {
            AttributeType createAttribute = null;

            if( val.TypeInfo.BuiltInType.Equals( BuiltInType.Byte ) )
            {
                byte builtInTypeByte = (byte)val.Value;
                string builtInTypeName = Enum.GetName( typeof( BuiltInType ), builtInTypeByte );

                string path = BuildLibraryReference( ATLPrefix, MetaModelName, "BuiltInType" );
                CAEXObject builtInTypeObject = m_cAEXDocument.FindByPath( path );
                AttributeFamilyType attributeDefinition = builtInTypeObject as AttributeFamilyType;


                if( attributeDefinition != null )
                {
                    createAttribute = seq[ "BuiltInType" ];
                    if( createAttribute == null )
                    {
                        createAttribute = seq.Append( "BuiltInType" );
                    }

                    createAttribute.RecreateAttributeInstance( attributeDefinition );
                    createAttribute.Name = "BuiltInType";
                    createAttribute.Value = builtInTypeName;
                    createAttribute.AttributeDataType = "xs:string";
                }
            }
            return createAttribute;
        }

        private AttributeType AddModifyAttributeEnum( AttributeSequence seq, 
            string name, 
            NodeId refDataType, 
            Variant val )
        {
            AttributeType attributeType = null;

            if( val.TypeInfo != null && val.TypeInfo.BuiltInType == BuiltInType.Int32 )
            {
                UANode dataTypeNode = FindNode<UANode>(refDataType);
                string dataTypeName = dataTypeNode.DecodedBrowseName.Name;
                string uri = m_modelManager.FindModelUri(dataTypeNode.DecodedNodeId);

                if( val.TypeInfo.ValueRank == ValueRanks.Scalar )
                {
                    int enumerationValue = (int)val.Value;
                    UADataType enumerationNode = FindNode<UADataType>(dataTypeNode.DecodedNodeId);
                    if (enumerationNode != null)
                    {
                        if (enumerationNode.Definition != null &&
                            enumerationNode.Definition.Field != null &&
                            enumerationNode.Definition.Field.Length > 0)
                        {
                            foreach (DataTypeField field in enumerationNode.Definition.Field)
                            {
                                if (field.Value == enumerationValue)
                                {
                                    Variant enumerationAsString = new Variant(field.Name);

                                    attributeType = AddModifyAttribute(seq,
                                        name,
                                        dataTypeName,
                                        enumerationAsString,
                                        bListOf: false,
                                        sURI: uri);

                                    break;
                                }
                            }
                        }
                    }
                }
                else if( val.TypeInfo.ValueRank >= ValueRanks.OneDimension )
                {
                    int[] enumerationValues = (int[])val.Value;
                    if( enumerationValues.Length > 0 )
                    {
                        string path = BuildLibraryReference(ATLPrefix, uri, ListOf + dataTypeName);
                        CAEXObject attributeList = m_cAEXDocument.FindByPath(path);
                        AttributeFamilyType listAttribute = attributeList as AttributeFamilyType;
                        attributeType = seq[name];  //find the existing attribute with the name
                        if (attributeType == null)
                        {
                            attributeType = seq.Append(name);  // not found so create a new one
                        }

                        attributeType.RecreateAttributeInstance(listAttribute);

                        IList valueAsList = val.Value as IList;
                        if (valueAsList != null)
                        {
                            for (int index = 0; index < valueAsList.Count; index++)
                            {
                                Variant elementVariant = new Variant(valueAsList[index]);
                                bool elementListOf = elementVariant.TypeInfo.ValueRank >= ValueRanks.OneDimension;
                                AddModifyAttribute(attributeType.Attribute, index.ToString(), refDataType,
                                    elementVariant );
                            }
                        }
                    }
                }
            }

            return attributeType;
        }


        private AttributeType AddModifyAttribute(AttributeSequence seq, string name, NodeId refDataType)
        {
            return AddModifyAttribute(seq, name, refDataType, Variant.Null);
        }


        private AttributeType OverrideBooleanAttribute(AttributeSequence seq,
            string AttributeName,
            Boolean value,
            bool typeOnly = false)
        {
            var at = AddModifyAttribute(seq, AttributeName, "Boolean", value);
            if (at != null && typeOnly)
            {
                at.AdditionalInformation.Append(OpcUaTypeOnly);
            }
            return at;
        }


        private string GetAttributeDataType(NodeId nodeId)
        {
            UANode baseNode;

            for (int i = 0; i < ua2xslookup_count; i++)
            {
                baseNode = m_modelManager.FindNodeByName(ua2xsLookup[i, ua2xslookup_uaname]);
                if( m_modelManager.IsTypeOf( nodeId, baseNode.DecodedNodeId ) )
                    return ua2xsLookup[ i, ua2xslookup_xsname ];
            }
            return "";
        }
        static private string BuildLibraryReference(string prefix, string namespaceURI, string elementName, string inverseName = null)
        {

            if (inverseName == null)
                return "[" + prefix + namespaceURI + "]/[" + elementName + "]";
            else
                return "[" + prefix + namespaceURI + "]/[" + elementName + "]/[" + inverseName + "]";

        }


        private string BaseRefFromNodeId(NodeId nodeId, string LibPrefix, bool UseInverseName = false, bool IsArray = false)
        {
            string NamespaceUri = m_modelManager.FindModelUri(nodeId);
            var BaseNode = m_modelManager.FindNode<UANode>(nodeId);
            if (BaseNode != null)
            {
                if (UseInverseName)
                {
                    var refnode = BaseNode as NodeSet.UAReferenceType;
                    if (refnode.InverseName != null)
                    {
                        if (BaseNode.DecodedBrowseName.Name != refnode.InverseName[0].Value)
                            return BuildLibraryReference(LibPrefix, NamespaceUri, BaseNode.DecodedBrowseName.Name, refnode.InverseName[0].Value);
                    }
                }
                if (IsArray == true)
                {
                    return BuildLibraryReference(LibPrefix, NamespaceUri, ListOf + BaseNode.DecodedBrowseName.Name);  //add ListOf
                }
                else
                {
                    return BuildLibraryReference(LibPrefix, NamespaceUri, BaseNode.DecodedBrowseName.Name);
                }
            }
            return "";
        }


        #region SUC
        void CopyAttributes(ref ExternalInterfaceType eit)
        {
            // var ob = eit.CAEXDocument.FindByPath(eit.RefBaseClassPath);
            var ob = m_cAEXDocument.FindByPath(eit.RefBaseClassPath);

            var iface = ob as InterfaceFamilyType;

            foreach (var e in iface.GetInheritedAttributes())
            {
                // No External Interface should have IsAbstract
                if( e.Name != "IsAbstract" )
                {
                    eit.Attribute.Insert( e );
                }
            }
        }

        private void AddNonHierarchicalReference( AttributeSequence sequence, string id )
        {
            AttributeType arrayAttribute = sequence[NonHierarchicalReferenceAttribute];
            if ( arrayAttribute != null )
            {
                AddModifyAttribute( arrayAttribute.Attribute, arrayAttribute.Attribute.Count.ToString(), 
                    "String", new Variant(id), bListOf: false);
            }
        }

        ExternalInterfaceType FindOrAddSourceInterface(ref SystemUnitClassType suc, 
            string uri, 
            string name, 
            NodeId nodeId,
            bool addAttribute = false)
        {
            string RefBaseClassPath = BuildLibraryReference(ICLPrefix, uri, name);
            var splitname = name.Split('/');
            var leafname = splitname[splitname.Length - 1];

            SystemUnitClassType working = suc;
            if (working != null)
            {
                bool bFoundInParent = false;
                bool bFirst = true;
                while (working != null && bFoundInParent == false)
                {
                    foreach (var iface in working.ExternalInterface)
                    {
                        if (iface.RefBaseClassPath == RefBaseClassPath)
                        {
                            if (bFirst)
                                return iface;
                            bFoundInParent = true;
                            break;
                        }
                    }
                    bFirst = false;
                    SystemUnitFamilyType family = working as SystemUnitFamilyType;
                    if (family != null)
                    {
                        working = family.BaseClass;
                    }
                    else
                    {
                        working = null;
                    }
                }
                if (bFoundInParent == true)  // make a unique name by appending the SUC name
                    leafname += ":" + suc.Name;
            }
            var rtn = suc.ExternalInterface.Append(leafname);
            rtn.ID = AmlIDFromNodeId(nodeId, leafname);
            rtn.RefBaseClassPath = RefBaseClassPath;
            CopyAttributes(ref rtn);
            if (addAttribute)
            {
                AddModifyAttribute(rtn.Attribute, NonHierarchicalReferenceAttribute,
                    "String", new Variant(), bListOf: true);
            }
            return rtn;
        }

        ExternalInterfaceType FindOrAddInterface(ref SystemUnitClassType suc, 
            string uri, 
            string name, 
            NodeId nodeId = null,
            bool addAttribute = false)
        {
            var splitname = name.Split('/');
            var leafname = splitname[splitname.Length - 1];
            if (leafname[0] == '[')
                leafname = leafname.Substring(1);  //remove the leading [
            SystemUnitClassType test = suc;

            foreach (var iface in test.ExternalInterface)
            {
                if (iface.Name == leafname)
                    return iface;
            }

            var rtn = suc.ExternalInterface.Append(leafname);
            rtn.RefBaseClassPath = BuildLibraryReference(ICLPrefix, uri, name);
            if (nodeId != null)
            {
                rtn.ID = AmlIDFromNodeId(nodeId, leafname);
            }
            CopyAttributes(ref rtn);
            if (addAttribute)
            {
                AddModifyAttribute(rtn.Attribute, NonHierarchicalReferenceAttribute,
                    "String", new Variant(), bListOf: true);
            }
            return rtn;
        }

        private string AmlIDFromNodeId(NodeId nodeId, string prefix = null)
        {
            AmlExpandedNodeId a = new AmlExpandedNodeId(nodeId, m_modelManager.FindModelUri(nodeId), prefix);
            return a.ToString();
         }

        private InternalElementType CreateClassInstanceWithIDReplacement(string prefix, SystemUnitFamilyType child)
        {
            InternalElementType internalElementType = child.CreateClassInstance();

            CompareLinksToExternaInterfaces( child, internalElementType);

            InternalElementSequence originalInternalElements = child.InternalElement;
            InternalElementSequence createdInternalElements = internalElementType.InternalElement;

            UpdateInternalElementChildIds(prefix, originalInternalElements, createdInternalElements);
            UpdateInternalElementParentIds(prefix, child, createdInternalElements);

            UpdateExternalInterfaceChildIds(prefix, child, internalElementType);

            return internalElementType;
        }

        private void UpdateInternalElementChildIds(
            string prefix,
            InternalElementSequence original,
            InternalElementSequence requiresUpdate)
        {
            if (original != null && requiresUpdate != null)
            {
                foreach (InternalElementType originalInternalElement in original)
                {
                    InternalElementType internalElementRequiresUpdate =
                        requiresUpdate[originalInternalElement.Name];

                    if (internalElementRequiresUpdate != null)
                    {
                        string nodeIdString = IsolateNodeId(originalInternalElement.ID);
                        string childPrefix = prefix + originalInternalElement.Name + "_";

                        internalElementRequiresUpdate.ID = childPrefix + nodeIdString;

                        // Does not create infinite loop
                        UpdateInternalElementChildIds(childPrefix, 
                            originalInternalElement.InternalElement,
                            internalElementRequiresUpdate.InternalElement);
                    }
                }
            }
        }

        private void UpdateInternalElementParentIds(
            string prefix,
            SystemUnitClassType original,
            InternalElementSequence requiresUpdate)
        {
            if (original != null && requiresUpdate != null)
            {
                List<string> namesWithGuidIds = new List<string>();
                Dictionary<string, InternalElementType> elementsWithGuidIds = new Dictionary<string, InternalElementType>();
                foreach (InternalElementType updateInternalElement in requiresUpdate)
                {
                    Guid guidId;
                    if (Guid.TryParse(updateInternalElement.ID, out guidId))
                    {
                        namesWithGuidIds.Add(updateInternalElement.Name);
                        elementsWithGuidIds.Add(updateInternalElement.Name, updateInternalElement);
                    }
                }

                if ( namesWithGuidIds.Count > 0)
                {
                    if (original.Reference != null)
                    {
                        foreach(InternalElementType more in original.Reference)
                        {
                            if ( elementsWithGuidIds.ContainsKey(more.Name) )
                            {
                                InternalElementType type = elementsWithGuidIds[more.Name];
                                string nodeIdString = IsolateNodeId(more.ID);
                                string addPrefix = prefix + more.Name + "_";
                                type.ID = addPrefix + nodeIdString;
                                // Now Go Down the hierarchy
                                UpdateInternalElementChildIds(addPrefix, more.InternalElement, type.InternalElement);
                            }
                        }
                        
                        UpdateInternalElementParentIds(prefix, original.Reference, requiresUpdate);
                    }
                }
            }
        }

        private void UpdateExternalInterfaceChildIds(
            string prefix,
            SystemUnitClassType original,
            SystemUnitClassType requiresUpdate)
        {
            if (original != null && requiresUpdate != null)
            {
                Dictionary<string, string> linkMap = new Dictionary<string, string>();
                // This logic only updates the link if it is all found
                Dictionary<string, string> linkMap2 = new Dictionary<string, string>();
                int requiredCounter = 0;
                foreach (InternalLinkType requiresUpdateLink in requiresUpdate.InternalLink)
                {
                    Guid currentId;
                    if (Guid.TryParse(requiresUpdateLink.RefPartnerSideB, out currentId))
                    {
                        InternalLinkType originalLink = original.InternalLink[requiresUpdateLink.Name];
                        if (originalLink != null)
                        {
                            string newParentLink = CreateUpdatedLinkName(prefix, originalLink.RefPartnerSideA);
                            string newThisLink = CreateUpdatedLinkName(prefix, originalLink.RefPartnerSideB);

                            if (!linkMap2.ContainsKey(requiresUpdateLink.RefPartnerSideA))
                            {
                                linkMap2.Add(requiresUpdateLink.RefPartnerSideA, newParentLink);
                            }
                            linkMap2.Add(requiresUpdateLink.RefPartnerSideB, newThisLink);

                            requiresUpdateLink.RefPartnerSideA = newParentLink;
                            requiresUpdateLink.RefPartnerSideB = newThisLink;
                        }
                        else
                        {
                            requiredCounter++;
                        }
                    }
                }

                foreach (ExternalInterfaceType externalInterface in requiresUpdate.ExternalInterface)
                {
                    string updatedId = "";
                    if (linkMap2.TryGetValue(externalInterface.ID, out updatedId))
                    {
                        externalInterface.ID = updatedId;
                    }
                }

                foreach (InternalElementType internalElement in requiresUpdate.InternalElement)
                {
                    foreach (ExternalInterfaceType externalInterface in internalElement.ExternalInterface)
                    {
                        string updatedId = "";
                        if (linkMap2.TryGetValue(externalInterface.ID, out updatedId))
                        {
                            externalInterface.ID = updatedId;
                        }
                    }
                }

                foreach (InternalElementType internalElement in requiresUpdate.InternalElement)
                {
                    InternalElementType originalInternalElement = original.InternalElement[internalElement.Name];
                    if (originalInternalElement != null)
                    {
                        string childPrefix = prefix + internalElement.Name + "_";
                        UpdateExternalInterfaceChildIds(childPrefix, originalInternalElement, internalElement);
                    }
                }

                if (requiredCounter > 0)
                {
                    //  This is working, however a limit is required.
                    UpdateExternalInterfaceChildIds(prefix,
                        original.Reference as SystemUnitClassType,
                        requiresUpdate as SystemUnitClassType);
                }
            }
        }

        private string CreateUpdatedLinkName(string prefix, string refString)
        {
            string link = refString;

            // Parse the name out (HasProperty, ComponentOf)
            string name = GetNodeIdPrefix(refString);
            string nodeId = IsolateNodeId(refString);

            return name + prefix + nodeId;
        }


        private string GetTypeNamePath(SystemUnitClassType type)
        {
            string name = "";

            if ( type.CAEXParent != null)
            {
                var baseClassParent = type.CAEXParent as SystemUnitClassLibType;
                
                // This prevents going down a useless path
                if ( baseClassParent == null)
                {
                    if (type.Reference != null)
                    {
                        name = GetTypeNamePath(type.Reference);
                    }
                }
            }

            name += type.Name + "_";

            return name;
        }

        private string IsolateNodeId(string nodeIdString)
        {
            string nodeId = nodeIdString;

            if (!nodeIdString.StartsWith("nsu%3D") )
            {
                int startIndex = nodeIdString.IndexOf("nsu%3D");
                if (startIndex > 0)
                {
                    nodeId = nodeIdString.Substring(startIndex);
                }
            }
            return nodeId;
        }

        private string GetNodeIdPrefix(string nodeIdString)
        {
            string nodeId = IsolateNodeId(nodeIdString);

            int nodeIdIndex = nodeIdString.IndexOf(nodeId);

            string prefix = nodeIdString.Substring(0, nodeIdIndex);

            return prefix;
        }

        private void CompareLinksToExternaInterfaces(SystemUnitClassType child, SystemUnitClassType checkIt)
        {
            // The externalInterfaces should be RefA in the links
            Dictionary<string, bool> externalInterfaces = new Dictionary<string, bool>();
            foreach (ExternalInterfaceType externalInterface in checkIt.ExternalInterface)
            {
                externalInterfaces.Add(externalInterface.ID, false);
            }

            foreach(InternalLinkType internalLink in checkIt.InternalLink)
            {
                if (externalInterfaces.ContainsKey(internalLink.RefPartnerSideA))
                {
                    externalInterfaces[internalLink.RefPartnerSideA] = true;
                }
            }

            bool outputAll = false;
            foreach(KeyValuePair<string, bool> entry in externalInterfaces)
            {
                if (!entry.Value) 
                {
                    outputAll = true;
                }
            }

            if ( outputAll )
            {
                bool deleted = true;
                while (deleted)
                {
                    deleted = false;
                    foreach (ExternalInterfaceType externalInterface in checkIt.ExternalInterface)
                    {
                        bool found = externalInterfaces[externalInterface.ID];

                        if (!found)
                        {
                            AttributeType theAttribute = externalInterface.Attribute[KeepExternalInterfaceAttribute];
                            if ( theAttribute == null )
                            {
                                checkIt.ExternalInterface.RemoveElement(externalInterface);
                                deleted = true;
                            }
                        }
                    }
                }
            }
        }

        string GetExternalInterfaceName(ExternalInterfaceSequence externalInterfaces, InternalLinkType internalLinkType, bool source)
        {
            string interfaceName = "";

            string internalLinkReference = internalLinkType.RefPartnerSideB;
            if ( source  )
            {
                internalLinkReference = internalLinkType.RefPartnerSideA;
            }

            foreach( ExternalInterfaceType externalInterfaceType in externalInterfaces)
            {
                if ( externalInterfaceType.ID == internalLinkReference )
                {
                    interfaceName = externalInterfaceType.Name;
                }
            }
            return interfaceName;
        }

        private struct InternalElementsAndLinks
        {
            public InternalElementType ElementType;
            public InternalLinkType LinkType;
        };

        InternalElementType GetReferenceInternalElement(
            ref SystemUnitClassLibType scl,
            ref RoleClassLibType rcl,
            SystemUnitFamilyType parent,
            NodeId parentNodeId,
            NodeId typedefNodeId,
            NodeId targetId)
        {
            string pathToType = GetTypeNamePath(parent);

            SystemUnitFamilyType typeDefSuc = null;

            if( typedefNodeId.Equals( parentNodeId ) )
            {
                typeDefSuc = parent;
            }
            else
            {
                typeDefSuc = FindOrAddSUC( ref scl, ref rcl, typedefNodeId );
            }

            string prefix = pathToType + typeDefSuc.Name + "_";

            SystemUnitFamilyType targetChild = null;

            if (!typedefNodeId.Equals(targetId))
            {
                targetChild = FindOrAddSUC(ref scl, ref rcl, targetId);
                prefix = pathToType + targetChild.Name + "_";
            }

            var typeDefSucCreated = CreateClassInstanceWithIDReplacement(prefix, typeDefSuc);

            if (typedefNodeId.Equals(targetId))
            {
                // No Work Required
                return typeDefSucCreated;
            }

            var targetCreated = CreateClassInstanceWithIDReplacement(prefix, targetChild);

            Dictionary<string, InternalElementsAndLinks> typeDefDictionary = new Dictionary<string, InternalElementsAndLinks>();
            Dictionary<string, InternalElementsAndLinks> targetDictionary = new Dictionary<string, InternalElementsAndLinks>();

            foreach (InternalElementType internalElementType in targetCreated.InternalElement)
            {
                InternalLinkType internalLinkType = targetCreated.InternalLink[internalElementType.Name];

                // Never seen null
                if (internalLinkType != null) 
                {
                    InternalElementsAndLinks addThis = new InternalElementsAndLinks();
                    addThis.ElementType = internalElementType;
                    addThis.LinkType = internalLinkType;
                    targetDictionary.Add(internalElementType.Name, addThis);
                }
            }

            foreach (InternalElementType internalElementType in typeDefSucCreated.InternalElement)
            {
                InternalLinkType internalLinkType = typeDefSucCreated.InternalLink[internalElementType.Name];
                // Never seen null
                if (internalLinkType != null)
                {
                    InternalElementsAndLinks addThis = new InternalElementsAndLinks();
                    addThis.ElementType = internalElementType;
                    addThis.LinkType = internalLinkType;
                    typeDefDictionary.Add(internalElementType.Name, addThis);
                }
            }

            Dictionary<string, ExternalInterfaceType> usedInterfaces = new Dictionary<string, ExternalInterfaceType>();
            Dictionary<string, InternalElementsAndLinks> usedInternalElements = new Dictionary<string, InternalElementsAndLinks>();

            foreach (KeyValuePair<string, InternalElementsAndLinks> entry in typeDefDictionary)
            {
                InternalLinkType internalLinkType = entry.Value.LinkType;
                foreach (ExternalInterfaceType externalInterfaceType in typeDefSucCreated.ExternalInterface)
                {
                    if (internalLinkType.RefPartnerSideA.Equals(externalInterfaceType.ID))
                    {
                        if (!usedInterfaces.ContainsKey(externalInterfaceType.Name))
                        {
                            usedInterfaces.Add(externalInterfaceType.Name, externalInterfaceType);
                        }

                        ExternalInterfaceType externalInterface = usedInterfaces[externalInterfaceType.Name];
                        
                        internalLinkType.RefPartnerSideA = externalInterface.ID;
                        usedInternalElements.Add(entry.Key, entry.Value);
                        break;
                    }
                }
            }

            foreach (KeyValuePair<string, InternalElementsAndLinks> entry in targetDictionary)
            {
                InternalLinkType internalLinkType = entry.Value.LinkType;
                foreach (ExternalInterfaceType externalInterfaceType in targetCreated.ExternalInterface)
                {
                    if (internalLinkType.RefPartnerSideA.Equals(externalInterfaceType.ID))
                    {
                        if (!usedInterfaces.ContainsKey(externalInterfaceType.Name))
                        {
                            usedInterfaces.Add(externalInterfaceType.Name, externalInterfaceType);
                        }
                        ExternalInterfaceType externalInterface = usedInterfaces[externalInterfaceType.Name];
                        internalLinkType.RefPartnerSideA = externalInterface.ID;
                        if (usedInternalElements.ContainsKey(entry.Key))
                        {
                            usedInternalElements[entry.Key] = entry.Value;
                        }
                        else
                        {
                            usedInternalElements.Add(entry.Key, entry.Value);
                        }

                        break;
                    }
                }
            }

            // Rebuild.
            typeDefSucCreated.ExternalInterface.Remove();
            typeDefSucCreated.InternalLink.Remove();
            typeDefSucCreated.InternalElement.Remove();

            foreach (ExternalInterfaceType externalInterface in usedInterfaces.Values)
            {
                typeDefSucCreated.ExternalInterface.Insert( externalInterface, asFirst: false, asIs: true );
            }

            foreach (KeyValuePair<string, InternalElementsAndLinks> entry in usedInternalElements)
            {
                typeDefSucCreated.InternalElement.Insert(entry.Value.ElementType, asFirst: false, asIs: true );
                typeDefSucCreated.InternalLink.Insert(entry.Value.LinkType, asFirst: false, asIs: true );
            }

            CompareLinksToExternaInterfaces(typeDefSucCreated, typeDefSucCreated);

            return typeDefSucCreated;
        }

        private void RebuildExternalInterfaces(
            SystemUnitFamilyType parent,
            SystemUnitClassType systemUnitClass)
        {
            string pathToType = GetTypeNamePath(parent);
            string prefix = pathToType + systemUnitClass.Name + "_";

            RebuildExternalInterfaces(prefix, systemUnitClass);
        }


        private void RebuildExternalInterfaces(
            string prefix,
            SystemUnitClassType systemUnitClass )
        {
            // The purpose here is to modify the InternalLinks and ExternalInterfaces to be more readable
            // from the perspective of the systemUnitClass itself.

            string named = ";" + prefix + WebUtility.UrlDecode(systemUnitClass.ID);

            Dictionary<string, string> oldIdToNewName = new Dictionary<string, string>();
            Dictionary<string,string>oldToNewName = new Dictionary<string, string>();
            Dictionary<string, ExternalInterfaceType> newTypes = new Dictionary<string, ExternalInterfaceType>();
            List<ExternalInterfaceType> reInsert = new List<ExternalInterfaceType>();

            foreach ( ExternalInterfaceType externalInterface in systemUnitClass.ExternalInterface )
            {
                AttributeType sourceType = externalInterface.Attribute["IsSource"];
                if ( sourceType != null && sourceType.Value != null && sourceType.Value.Equals("true"))
                {
                    string[] splitName = externalInterface.Name.Split(":");
                    if (splitName.Length > 0)
                    {
                        string newName = splitName[0];
                        if (!oldToNewName.ContainsKey(externalInterface.Name))
                        {
                            oldIdToNewName.Add(externalInterface.ID, newName);
                            oldToNewName.Add(externalInterface.Name, newName);
                            if (!newTypes.ContainsKey(newName))
                            {
                                ExternalInterfaceType replace = (ExternalInterfaceType)externalInterface.Copy(deepCopy: true);
                                replace.Name = newName;
                                replace.ID = WebUtility.UrlEncode(newName + named);
                                newTypes.Add(newName, replace);
                            }
                        }
                    }
                }
                else 
                {
                    AttributeType keepAttribute = externalInterface.Attribute[KeepExternalInterfaceAttribute];
                    if (keepAttribute != null)
                    {
                        reInsert.Add(externalInterface);
                    }
                }
            }

            foreach( InternalLinkType internalLink in systemUnitClass.InternalLink)
            {
                string newName;
                if ( oldIdToNewName.TryGetValue(internalLink.RefPartnerSideA, out newName) )
                {
                    ExternalInterfaceType newType;
                    if ( newTypes.TryGetValue(newName, out newType) )
                    {
                        internalLink.RefPartnerSideA = newType.ID;
                    }
                }
            }

            // Now wipe the externalInterfaces and add all the new ones
            systemUnitClass.ExternalInterface.Remove();
            foreach( ExternalInterfaceType externalInterface in newTypes.Values )
            {
                systemUnitClass.ExternalInterface.Insert( externalInterface, asFirst: false, asIs: true );
            }
            foreach (ExternalInterfaceType externalInterface in reInsert)
            {
                systemUnitClass.ExternalInterface.Insert(externalInterface, asFirst: false, asIs: true);
            }
        }

        private void UpdateIsAbstract( UANode targetNode, SystemUnitClassType systemUnitClass )
        {
            bool isTargetAbstract = false;
            UAType targetType = targetNode as UAType;
            if( targetType != null && targetType.IsAbstract )
            {
                isTargetAbstract = true;
            }

            if( !isTargetAbstract )
            {
                AttributeType isAbstractAttribute = systemUnitClass.Attribute[ "IsAbstract" ];
                if( isAbstractAttribute != null )
                {
                    systemUnitClass.Attribute.RemoveElement( isAbstractAttribute );
                }
            }
        }

        private string GetExistingCreatedPathName(UANode node)
        {
            string createdPathName = "";

            string uri = m_modelManager.FindModelUri(node.DecodedNodeId);
            Dictionary<string, string> uriMap;
            if (LookupNames.TryGetValue(uri, out uriMap))
            {
                string nodePath;
                if (uriMap.TryGetValue(node.DecodedNodeId.ToString(), out nodePath))
                {
                    createdPathName = nodePath;
                }
            }

            return createdPathName;
        }

        private string GetCreatedPathName(UANode node)
        {
            string pathName = GetExistingCreatedPathName(node);

            if (pathName.Length == 0)
            {
                UAInstance uaInstance = node as UAInstance;
                if (uaInstance != null  )
                {
                    var refList = m_modelManager.FindReferences( node.DecodedNodeId );
                    foreach( var reference in refList )
                    {
                        if( reference.IsForward == false && 
                            ( reference.ReferenceTypeId.Equals(HasPropertyNodeId) ||
                            reference.ReferenceTypeId.Equals( Opc.Ua.ReferenceTypeIds.HasComponent ) ) )
                        {
                            UANode parentNodeId = m_modelManager.FindNode<UANode>( reference.TargetId );
                            if ( parentNodeId != null )
                            {
                                pathName = GetCreatedPathName(parentNodeId);
                                break;
                            }
                        }
                    }
                }

                if (pathName.Length > 0)
                {
                    pathName += "_";
                }

                pathName += node.DecodedBrowseName.Name;

                string uri = m_modelManager.FindModelUri(node.DecodedNodeId);
                if (!LookupNames.ContainsKey(uri))
                {
                    LookupNames.Add(uri, new Dictionary<string, string>());
                }

                string nodeIdString = node.DecodedNodeId.ToString();

                Dictionary<string, string> uriMap = LookupNames[uri];

                // It's still possible that this has already been added, as it is a recursive method
                string existingPathName;
                if (!uriMap.TryGetValue(nodeIdString, out existingPathName))
                {
                    uriMap.Add(nodeIdString, pathName);
                }
            }

            return pathName;
        }


        SystemUnitFamilyType FindOrAddSUC(ref SystemUnitClassLibType scl, ref RoleClassLibType rcl, NodeId nodeId)
        {
            var refnode = FindNode<NodeSet.UANode>(nodeId);
            string path = "";
            string createdPathName = GetCreatedPathName( refnode );
            if (refnode.NodeClass != NodeClass.Method)
                path = BuildLibraryReference(SUCPrefix, 
                    m_modelManager.FindModelUri(refnode.DecodedNodeId), 
                    GetCreatedPathName(refnode));
            SystemUnitFamilyType rtn = scl.CAEXDocument.FindByPath(path) as SystemUnitFamilyType;

            if (rtn == null)
            {
                Utils.LogTrace( "FindOrAddSuc - {0}:{1} {2} Begin", refnode.BrowseName, refnode.NodeId, createdPathName );

                if (m_modelManager.IsTypeOf(nodeId, BaseInterfaceNodeId) == true)
                {
                    var rc = rcl.New_RoleClass(refnode.DecodedBrowseName.Name);  // create a RoleClass for UA interfaces
                    rc.ID = AmlIDFromNodeId(nodeId, RoleClassPrefix);
                    if (nodeId == BaseInterfaceNodeId)
                        rc.RefBaseClassPath = RCLPrefix + MetaModelName + "/" + UaBaseRole;
                    else
                        rc.RefBaseClassPath = BuildLibraryReference(RCLPrefix, m_modelManager.FindModelUri(BaseInterfaceNodeId), "BaseInterfaceType");
                }
                // make sure the base type is already created
                NodeId BaseNodeId = m_modelManager.FindFirstTarget(refnode.DecodedNodeId, HasSubTypeNodeId, false);
                if (BaseNodeId != null)
                {
                    var refBaseNode = FindNode<NodeSet.UANode>(BaseNodeId);
                    string basepath = BuildLibraryReference(SUCPrefix, m_modelManager.FindModelUri(refBaseNode.DecodedNodeId), refBaseNode.DecodedBrowseName.Name);
                    SystemUnitFamilyType baseSUC = scl.CAEXDocument.FindByPath(basepath) as SystemUnitFamilyType;
                    if (baseSUC == null)
                        
                        FindOrAddSUC(ref scl, ref rcl, BaseNodeId);

                }
                // now add the SUC with the immediate elements
                // starting with the attributes

                rtn = scl.SystemUnitClass.Append(refnode.DecodedBrowseName.Name);
                // Helpful for Debugging
                //AddModifyAttribute(rtn.Attribute, "NodeId", "String", AmlIDFromNodeId(nodeId));
                if (BaseNodeId != null)
                {
                    rtn.ID = AmlIDFromNodeId(nodeId);

                    rtn.RefBaseClassPath = BaseRefFromNodeId(BaseNodeId, SUCPrefix);

                    // override any attribute values

                    var basenode = FindNode<NodeSet.UANode>(BaseNodeId);
                    AddBaseNodeClassAttributes(rtn.Attribute, refnode);
                    switch (refnode.NodeClass)
                    {
                        case NodeClass.ObjectType:
                            var obnode = refnode as NodeSet.UAObjectType;
                            var baseob = basenode as NodeSet.UAObjectType;
                            if (baseob.IsAbstract != obnode.IsAbstract)
                                OverrideBooleanAttribute(rtn.Attribute, "IsAbstract", obnode.IsAbstract);
                            break;


                        case NodeClass.VariableType:
                            var varnode = refnode as NodeSet.UAVariableType;
                            var basevar = basenode as NodeSet.UAVariableType;

                            if (varnode.ValueRank != basevar.ValueRank)
                                AddModifyAttribute(rtn.Attribute, "ValueRank", "Int32", varnode.ValueRank);
                            if (basevar.IsAbstract != varnode.IsAbstract)
                                OverrideBooleanAttribute(rtn.Attribute, "IsAbstract", varnode.IsAbstract);
                            if (basevar.DataType != varnode.DataType)
                                AddModifyAttribute(rtn.Attribute, "Value", varnode.DecodedDataType);
                            break;

                    }
                }
                else
                {
                    // add the attributes to the base SUC Class
                    switch (refnode.NodeClass)
                    {
                        case NodeClass.ObjectType:
                            rtn.ID = AmlIDFromNodeId( nodeId );

                            // AddModifyAttribute(rtn.Attribute, "EventNotifier", "EventNotifierType", Variant.Null); // #9 remove unset attributes
                            AddBaseNodeClassAttributes(rtn.Attribute, refnode);
                            break;
                        case NodeClass.VariableType:
                            rtn.ID = AmlIDFromNodeId( nodeId );

                            // AddModifyAttribute(rtn.Attribute, "ArrayDimensions", "ListOfUInt32", Variant.Null); // #9 remove unset attributes
                            // AddModifyAttribute(rtn.Attribute, "ValueRank", "Int32", -2); // #9 remove unset attributes
                            // AddModifyAttribute(rtn.Attribute, "Value", "BaseDataType", Variant.Null); // #9 remove unset attributes
                            // AddModifyAttribute(rtn.Attribute, "AccessLevel", "AccessLevelType", Variant.Null); // #9 remove unset attributes
                            // AddModifyAttribute(rtn.Attribute, "MinimumSamplingInterval", "Duration", Variant.Null); // #9 remove unset attributes
                            AddBaseNodeClassAttributes(rtn.Attribute, refnode);
                            break;
                        case NodeClass.Method:
                            AddBaseNodeClassAttributes(rtn.Attribute, refnode);

                            break;
                    }
                                     
                }


                // now add the references and contained objects
                var refList = m_modelManager.FindReferences(nodeId);
                foreach (var reference in refList)
                {
                    if (reference.IsForward == true)
                    {
                        if (m_modelManager.IsTypeOf(reference.ReferenceTypeId, AggregatesNodeId) == true)
                        {
                            string refURI = m_modelManager.FindModelUri(reference.ReferenceTypeId);
                            var ReferenceTypeNode = FindNode<UANode>(reference.ReferenceTypeId);
                            SystemUnitClassType rtnSystemUnitClassType = rtn as SystemUnitClassType;
                            var sourceInterface = FindOrAddSourceInterface(ref rtnSystemUnitClassType, 
                                refURI, ReferenceTypeNode.DecodedBrowseName.Name, nodeId);
                            var targetNode = FindNode<UANode>(reference.TargetId);
                            //                           if (targetNode.NodeClass != NodeClass.Method) //  methods are now processed
                            {
                                var TypeDefNodeId = reference.TargetId;
                                if (targetNode.NodeClass == NodeClass.Variable || targetNode.NodeClass == NodeClass.Object)
                                    TypeDefNodeId = m_modelManager.FindFirstTarget(reference.TargetId, HasTypeDefinitionNodeId, true);
                                if (TypeDefNodeId == null)
                                    TypeDefNodeId = reference.TargetId;

                                var ie = GetReferenceInternalElement(ref scl, ref rcl,
                                    rtn, nodeId, TypeDefNodeId, reference.TargetId);

                                ie.Name = targetNode.DecodedBrowseName.Name;
                                ie.ID = AmlIDFromNodeId(reference.TargetId);

                                UpdateIsAbstract( targetNode, ie );

                                RebuildExternalInterfaces(rtn, ie);

                                rtn.AddInstance(ie);

                                var basenode = FindNode<NodeSet.UANode>(TypeDefNodeId);                               
                                AddBaseNodeClassAttributes(ie.Attribute, targetNode);
                                if (targetNode.NodeClass == NodeClass.Variable)
                                {  //  Set the datatype for Value
                                    var varnode = targetNode as NodeSet.UAVariable;
                                    bool bListOf = (varnode.ValueRank >= ValueRanks.OneDimension);  // use ListOf when its a UA array
                                    AddModifyAttribute(ie.Attribute, "Value", varnode.DecodedDataType, varnode.DecodedValue, bListOf);
                                    AddModifyAttribute( ie.Attribute, "ValueRank", "Int32", varnode.ValueRank );
                                    SetArrayDimensions( ie, varnode.ArrayDimensions );


                                    UpdateDerived( ref ie, TypeDefNodeId, reference.TargetId );
                                }
                                else if (targetNode.NodeClass == NodeClass.Method)
                                    ie.RefBaseSystemUnitPath = BuildLibraryReference(SUCPrefix, MetaModelName, MethodNodeClass);
                                SystemUnitClassType ie_suc = ie as SystemUnitClassType;

                                var destInterface = FindOrAddInterface(ref ie_suc, refURI, 
                                    ReferenceTypeNode.DecodedBrowseName.Name + "]/[" + sourceInterface.Attribute["InverseName"].Value, 
                                    reference.TargetId);

                                var internalLink = rtn.New_InternalLink(targetNode.DecodedBrowseName.Name);
                                internalLink.RefPartnerSideA = sourceInterface.ID;
                                internalLink.RefPartnerSideB = destInterface.ID;

                                //   set the modeling rule
                                var modellingId = m_modelManager.FindFirstTarget(reference.TargetId, HasModellingRuleNodeId, true);
                                var modellingRule = m_modelManager.FindNode<UANode>(modellingId);
                                if (modellingRule != null)
                                    AddModifyAttribute(destInterface.Attribute, "ModellingRule", "ModellingRuleType", modellingRule.DecodedBrowseName.Name, false, MetaModelName);
                                
                            }
                        }
                        else if (m_modelManager.IsTypeOf(reference.ReferenceTypeId, HasInterfaceNodeId) == true)
                        {
                            // add the elements of the UA Interface
                            var targetNode = FindNode<UANode>(reference.TargetId);
                            string rolepath = BuildLibraryReference(RCLPrefix, m_modelManager.FindModelUri(targetNode.DecodedNodeId), targetNode.DecodedBrowseName.Name);
                            var roleSUC = FindOrAddSUC(ref scl, ref rcl, reference.TargetId);  // make sure the AMLobjects are already created.
                            var srt = rtn.New_SupportedRoleClass(rolepath, false);
                            var inst = roleSUC.CreateClassInstance();
                            foreach (var element in inst.InternalElement)
                            {
                                rtn.InternalElement.Append(element);
                            }
                        }
                        else if (m_modelManager.IsTypeOf(reference.ReferenceTypeId,
                            Opc.Ua.ReferenceTypeIds.NonHierarchicalReferences) == true)
                        {
                            _nonHierarchicalReferences.AddReference(reference, instance: _runningInstances);
                        }
                    }
                }
                rtn.New_SupportedRoleClass(RCLPrefix + MetaModelName + "/" + UaBaseRole, false);  // all UA SUCs support the UaBaseRole

                Utils.LogTrace( "FindOrAddSuc - {0}:{1} {2} Complete", refnode.BrowseName, refnode.NodeId, createdPathName );
            }

            return rtn;
        }

        private void SetArrayDimensions( SystemUnitClassType element, string arrayDimensions )
        {
            SetArrayDimensions( element.Attribute, arrayDimensions );
        }

        private void SetArrayDimensions( AttributeSequence attributes, string arrayDimensions )
        {
            string[] parts = arrayDimensions.Split( ',' );
            List<uint> arrayValues = new List<uint>();
            foreach( string part in parts )
            {
                UInt32 value;
                if( UInt32.TryParse( part, out value ) )
                {
                    arrayValues.Add( value );
                }
            }

            if (arrayValues.Count > 0)
            {
                AddModifyAttribute(attributes,
                    "ArrayDimensions",
                    Opc.Ua.DataTypeIds.UInt32,
                    new Variant(arrayValues.ToArray()),
                    bListOf: true);
            }
        }

        private void AddNonHierarchicalReferences()
        {
            foreach (NonHierarchicalReferenceHolder referenceHolder in _nonHierarchicalReferences.ReferenceList)
            {
                UANode sourceNode = m_modelManager.FindNode<UANode>(referenceHolder.Reference.SourceId);
                SystemUnitClassType sourceSystemUnitClass = FindNonHierarchicalReference(referenceHolder,
                    sourceNode);
                string sourcePath = GetCreatedPathName(sourceNode);

                UANode targetNode = m_modelManager.FindNode<UANode>(referenceHolder.Reference.TargetId);
                SystemUnitClassType targetSystemUnitClass = FindNonHierarchicalReference(referenceHolder,
                    targetNode);
                string targetPath = GetCreatedPathName(targetNode);

                string refURI = m_modelManager.FindModelUri(referenceHolder.Reference.ReferenceTypeId);
                UANode referenceTypeNode = FindNode<UANode>(referenceHolder.Reference.ReferenceTypeId);

                Utils.LogTrace("{0} : {1} {2} - {3} {4}",
                    referenceTypeNode.DecodedBrowseName.Name,
                    sourceSystemUnitClass.Name,
                    sourceSystemUnitClass.ID,
                    targetSystemUnitClass.Name,
                    targetSystemUnitClass.ID);

                ExternalInterfaceType sourceInterface = FindOrAddSourceInterface(ref sourceSystemUnitClass,
                    refURI, 
                    referenceTypeNode.DecodedBrowseName.Name, 
                    referenceHolder.Reference.SourceId,
                    addAttribute: true);

                // Rebuild the source name
                string preId = referenceTypeNode.DecodedBrowseName.Name + ";" +
                    sourcePath + "_" + WebUtility.UrlDecode(sourceSystemUnitClass.ID);

                string newId = WebUtility.UrlEncode(preId);
                sourceInterface.ID = newId;

                ExternalInterfaceType destInterface = FindOrAddInterface(ref targetSystemUnitClass,
                    refURI, 
                    referenceTypeNode.DecodedBrowseName.Name + "]/[" + sourceInterface.Attribute["InverseName"].Value,
                    referenceHolder.Reference.TargetId,
                    addAttribute: true);

                AddNonHierarchicalReference(sourceInterface.Attribute, targetSystemUnitClass.ID);
                AddNonHierarchicalReference(destInterface.Attribute, sourceSystemUnitClass.ID);

                
                // Archie October 2025
                // InternalLink Problems are the reason for the non-aml way to solve for
                // non-hierarchical references.  Some InternalLinks work fine, most do not.
                // This code allowed for the creation of the InternalLink for those scenarios that did work
                // The working group has decided to not use InternalLinks for non-hierarchical references
                // to have a consistent mechanism.
                //if (!referenceHolder.Limited)
                //{
                //    InternalLinkType internalLink = sourceSystemUnitClass.New_InternalLink(
                //        referenceTypeNode.DecodedBrowseName.Name);
                //    internalLink.RefPartnerSideA = sourceInterface.ID;
                //    internalLink.RefPartnerSideB = destInterface.ID;
                //}
                
            }
        }

        private SystemUnitClassType FindNonHierarchicalReference(NonHierarchicalReferenceHolder reference, UANode node)
        {
            SystemUnitClassType type = null;

            if (reference.Instance)
            {
                // The node should have exactly what I want.
                string amlNode = AmlIDFromNodeId(node.DecodedNodeId);
                CAEXObject cAEXObject = m_cAEXDocument.FindByID(amlNode, resolveAlias: false, elementType: typeof(InternalElementType));
                type = cAEXObject as SystemUnitClassType;
            }
            else
            {
                string createdPathName = GetCreatedPathName(node);
                string[] paths = createdPathName.Split('_');

                if (paths.Length > 0)
                {
                    string uri = m_modelManager.FindModelUri(node.DecodedNodeId);
                    string firstPath = BuildLibraryReference(SUCPrefix, uri, paths[0]);
                    SystemUnitClassType initialDestination = m_cAEXDocument.FindByPath(firstPath) as SystemUnitClassType;

                    if (initialDestination != null)
                    {
                        SystemUnitClassType working = initialDestination;
                        for (int index = 1; index < paths.Length; index++)
                        {
                            SystemUnitClassType potential = working.InternalElement[paths[index]] as SystemUnitClassType;
                            if (potential != null)
                            {
                                working = potential as SystemUnitClassType;
                            }                      
                        }

                        type = working;
                    }
                }
            }
            return type;
        }

        #endregion


        #region ICL

        private void OverrideAttribute(IClassWithBaseClassReference owner, 
            string Name, 
            string AttType, 
            object value)
        {
            var atts = owner.GetInheritedAttributes();
            foreach (var aa in atts)
            {
                if (aa.Name == Name)
                {
                    if (aa.AttributeDataType == AttType && aa.AttributeValue.Equals(value))
                    {
                        return;  // no need to override
                    }
                }
            }

            AttributeType a = owner.Attribute.Append();
            a.Name = Name;
            a.AttributeDataType = AttType;
            a.AttributeValue = value;

            a.AdditionalInformation.Append(OpcUaTypeOnly);
        }

        private void ProcessReferenceType(ref InterfaceClassLibType icl, NodeId nodeId)
        {
            var refnode = FindNode<NodeSet.UAReferenceType>(nodeId);
            var added = icl.InterfaceClass.Append(refnode.DecodedBrowseName.Name);
            added.ID = AmlIDFromNodeId(nodeId, ForwardPrefix);
            NodeId BaseNodeId = m_modelManager.FindFirstTarget(refnode.DecodedNodeId, HasSubTypeNodeId, false);
            if (BaseNodeId != null)
            {
                added.RefBaseClassPath = BaseRefFromNodeId(BaseNodeId, ICLPrefix);
                InterfaceClassType ict = icl.CAEXDocument.FindByPath(added.RefBaseClassPath) as InterfaceClassType;
                if (ict == null)
                    ProcessReferenceType(ref icl, BaseNodeId);
            }
            else
            {
                added.RefBaseClassPath = "AutomationMLInterfaceClassLib/AutomationMLBaseInterface";

             //   AddModifyAttribute(added.Attribute, "InverseName", "LocalizedText", Variant.Null);
             //   AddModifyAttribute(added.Attribute, "ModellingRule", "ModellingRuleType", Variant.Null, false, MetaModelName);
             //   OverrideBooleanAttribute(added.Attribute, "Symmetric", true);
             //   OverrideBooleanAttribute(added.Attribute, "IsAbstract", true);
             //   OverrideAttribute(added, IsSource, "xs:boolean", true);
                OverrideAttribute(added, RefClassConnectsToPath, "xs:string", added.CAEXPath());

            }
            // look for inverse name
            InterfaceFamilyType inverseAdded = null;
            if (refnode.InverseName != null)
            {
                if (refnode.Symmetric == false && refnode.InverseName[0].Value != refnode.DecodedBrowseName.Name)
                {
                    inverseAdded = added.InterfaceClass.Append(refnode.InverseName[0].Value);
                    inverseAdded.ID = AmlIDFromNodeId(nodeId, ReversePrefix);
                    if (BaseNodeId != null)
                        inverseAdded.RefBaseClassPath = BaseRefFromNodeId(BaseNodeId, ICLPrefix, true);
                }
            }

            AddIsAbstractAttribute(added.Attribute, refnode.IsAbstract);

            // override any attribute values
            if (BaseNodeId != null)
            {
                var basenode = FindNode<NodeSet.UAReferenceType>(BaseNodeId);

                if (basenode.Symmetric != refnode.Symmetric)
                {
                    RemoveUnwantedNodeIdAttribute(
                        OverrideBooleanAttribute(
                            added.Attribute, "Symmetric", refnode.Symmetric, typeOnly: true));
                }

                if (refnode.InverseName != null)
                {
                    AttributeType inverseNameAttribute = AddModifyAttribute(added.Attribute, 
                        "InverseName", "LocalizedText", refnode.InverseName[0].Value);
                    RemoveUnwantedNodeIdAttribute(inverseNameAttribute);
                    inverseNameAttribute.AdditionalInformation.Append(OpcUaTypeOnly);
                }

                // Need typeonly here
                OverrideAttribute(added, IsSource, "xs:boolean", value: true);
                OverrideAttribute(added, RefClassConnectsToPath, "xs:string", 
                    (inverseAdded != null ? inverseAdded.CAEXPath() : added.CAEXPath()));

                if (inverseAdded != null)
                {
                    if (basenode.IsAbstract != refnode.IsAbstract)
                    {
                        RemoveUnwantedNodeIdAttribute(
                            OverrideBooleanAttribute(
                                inverseAdded.Attribute, "IsAbstract", refnode.IsAbstract, typeOnly: true));
                    }
                    if (basenode.Symmetric != refnode.Symmetric)
                    {
                        RemoveUnwantedNodeIdAttribute(
                            OverrideBooleanAttribute(
                                inverseAdded.Attribute, "Symmetric", refnode.Symmetric, typeOnly: true));
                    }

                    AttributeType inverseNameAttribute = AddModifyAttribute(
                        inverseAdded.Attribute, "InverseName", "LocalizedText", refnode.DecodedBrowseName.Name);
                    RemoveUnwantedNodeIdAttribute(inverseNameAttribute);
                    inverseNameAttribute.AdditionalInformation.Append(OpcUaTypeOnly);

                    OverrideAttribute(inverseAdded, IsSource, "xs:boolean", false);
                    OverrideAttribute(inverseAdded, RefClassConnectsToPath, "xs:string", added.CAEXPath());
                }
            }

            AttributeType nodeIdAttribute = AddModifyAttribute(added.Attribute, "NodeId", "NodeId", new Variant( nodeId ) );
            nodeIdAttribute.AdditionalInformation.Append( OpcUaTypeOnly );
            MinimizeNodeId( nodeIdAttribute );
        }


        /// <summary>
        /// Recreate the attribute instance to remove any inherited attributes that are not part of the base type.
        /// These are automatically added by the Aml Engine.  This causes a huge problem with IsAbstract and NodeId
        /// </summary>
        /// <param name="sourceAttribute"></param>
        /// <param name="newAttribute"></param>
        /// <returns></returns>
        private AttributeType RecreateAttributeInstance( 
            AttributeFamilyType sourceAttribute, AttributeType newAttribute )
        {
            newAttribute.RecreateAttributeInstance(sourceAttribute);

            List<AttributeType> inheritedAttributes = new List<AttributeType>();

            foreach(AttributeType attribute in newAttribute.Attribute)
            {
                if (sourceAttribute.Attribute[attribute.Name] == null)
                {
                    inheritedAttributes.Add(attribute);
                }
            }

            foreach(AttributeType attribute in inheritedAttributes)
            {
                newAttribute.Attribute.RemoveElement(attribute);
            }

            return newAttribute;
        }

        private void RemoveUnwantedAttribute(AttributeType attributeType, string attributeName)
        {
            if (attributeType != null)
            {
                RemoveUnwantedAttribute(attributeType.Attribute, attributeName);
            }
        }

        private void RemoveUnwantedAttribute(AttributeSequence attributes, string attributeName)
        {
            if (attributes != null)
            {
                AttributeType unwantedAttribute = attributes[attributeName];
                if (unwantedAttribute != null)
                {
                    attributes.RemoveElement(unwantedAttribute);
                }
            }
        }

        private void AddIsAbstractAttribute( AttributeSequence sequence
            , bool isAbstract )
        {
            if (sequence != null )
            {
                AttributeType isAbstractAttribute = sequence["IsAbstract"];
                if ( isAbstractAttribute != null && !isAbstract )
                {
                    sequence.RemoveElement(isAbstractAttribute);
                }

                if ( isAbstract )
                {
                    isAbstractAttribute = OverrideBooleanAttribute(sequence, "IsAbstract", true, typeOnly: true);
                    // Now, because of the way the library recreates attributes, it will come back with nodeid
                    // due to adding inherited Attributes.
                    RemoveUnwantedAttribute(isAbstractAttribute.Attribute, "NodeId");
                }
            }
        }


        private void RemoveUnwantedNodeIdAttribute(AttributeType attribute)
        {
            RemoveUnwantedAttribute(attribute, "NodeId");
        }

        private void RemoveNodeIdsFromDefinition(AttributeType attribute)
        {
            if (attribute != null)
            {
                foreach (AttributeType indexAttribute in attribute.Attribute)
                {
                    RemoveUnwantedNodeIdAttribute(indexAttribute);
                    foreach (AttributeType fieldAttribute in indexAttribute.Attribute)
                    {
                        RemoveUnwantedNodeIdAttribute(fieldAttribute);
                    }
                }
            }
        }

        #endregion


        #region ATL
        private AttributeFamilyType CreateListOf(AttributeFamilyType aft)
        {
            var rtn = new AttributeFamilyType(new System.Xml.Linq.XElement(defaultNS + "AttributeType"));
            rtn.Name = ListOf + aft.Name;
            m_atl_temp.AttributeType.Insert(rtn);
            rtn.RefAttributeType = "AutomationMLBaseAttributeTypeLib/OrderedListType";

            /* Mantis #7383 - remove placeholder elements of ListOf types
            AttributeType a = new AttributeType(new System.Xml.Linq.XElement(defaultNS + "Attribute"));
            
            a.RecreateAttributeInstance(aft);
            a.Name = aft.Name;
           rtn.Insert(a);
            end Mantis #7383 */

            return rtn;
        }


        private void ProcessEnumerations(ref AttributeTypeType att, NodeId nodeId)
        {
            NodeId enumTypeNodeId = m_modelManager.FindFirstTarget(nodeId, HasPropertyNodeId, true, "EnumStrings");
            if (enumTypeNodeId == null)
            {
                enumTypeNodeId = m_modelManager.FindFirstTarget(nodeId, HasPropertyNodeId, true, "EnumValues");
            }

            if (enumTypeNodeId != null)
            {
                att.AttributeDataType = "xs:string";

                UADataType MyNode = FindNode<UADataType>(nodeId);

                if (MyNode.Definition != null && MyNode.Definition.Field != null && MyNode.Definition.Field.Length > 0)
                {
                    AttributeValueRequirementType stringValueRequirement = new AttributeValueRequirementType(new System.Xml.Linq.XElement(defaultNS + "Constraint"));

                    stringValueRequirement.Name = MyNode.DecodedBrowseName.Name + " Constraint";
                    NominalScaledTypeType stringValueNominalType = stringValueRequirement.New_NominalType();

                    foreach (DataTypeField field in MyNode.Definition.Field)
                    {
                        stringValueNominalType.RequiredValue.Append(field.Name);
                    }

                    att.Constraint.Insert(stringValueRequirement);
                }
            }
        }

        private void ProcessOptionSets(ref AttributeTypeType att, NodeId nodeId)
        {
            // This should not be done via the Localized text.  Should be from Field Definitions.
            UADataType dataType = m_modelManager.FindNode<UADataType>(nodeId);
            if ( dataType != null )
            {
                if (dataType != null &&
                    dataType.Definition != null &&
                    dataType.Definition.IsOptionSet == true)
                {
                    if (dataType.Definition.Field != null)
                    {
                        foreach(DataTypeField field in dataType.Definition.Field)
                        {
                            if ( !string.IsNullOrEmpty( field.Name ))
                            {
                                AttributeType a = new AttributeType(new System.Xml.Linq.XElement(defaultNS + "Attribute"));
                                a.Name = field.Name;
                                a.AttributeDataType = "xs:boolean";
                                att.Attribute.Insert(a, false);
                            }
                        }
                    }
                }
            }

            if (nodeId == NodeIdNodeId || nodeId == ExpandedNodeIdNodeId)
            {
                // add NodeId
                var added2 = new AttributeType(new System.Xml.Linq.XElement(defaultNS + "Attribute"));
                added2.Name = "ServerInstanceUri";
                added2.AttributeDataType = "xs:anyURI";
                att.Attribute.Insert(added2, false);

                added2 = new AttributeType(new System.Xml.Linq.XElement(defaultNS + "Attribute"));
                added2.Name = "Alias";
                string path = BuildLibraryReference(ATLPrefix, MetaModelName, "Alias");
                var ob = m_cAEXDocument.FindByPath(path);
                var at = ob as AttributeFamilyType;
                added2.RecreateAttributeInstance(at);
                att.Attribute.Insert(added2, false);

                added2 = new AttributeType(new System.Xml.Linq.XElement(defaultNS + "Attribute"));
                added2.Name = "RootNodeId";
                path = BuildLibraryReference(ATLPrefix, MetaModelName, "ExplicitNodeId");
                ob = m_cAEXDocument.FindByPath(path);
                at = ob as AttributeFamilyType;
                added2.RecreateAttributeInstance(at);
                att.Attribute.Insert(added2, false);

                added2 = new AttributeType(new System.Xml.Linq.XElement(defaultNS + "Attribute"));
                added2.Name = "BrowsePath";
                path = BuildLibraryReference(ATLPrefix, uaNamespaceURI, "RelativePath");
                added2.RefAttributeType = BaseRefFromNodeId(RelativePathNodeId, ATLPrefix);

                var b = added2 as AttributeTypeType;
                RecurseStructures(ref b, RelativePathNodeId );
                
                att.Attribute.Insert(added2, false);


            }
            else if (nodeId == QualifiedNameNodeId)
            {
                string namespaceUriPath = BuildLibraryReference(ATLPrefix, MetaModelName, "NamespaceUri");
                AttributeFamilyType namespaceUriRoot = m_cAEXDocument.FindByPath(namespaceUriPath) as AttributeFamilyType;

                AttributeType newNamespaceUri = new AttributeType(new System.Xml.Linq.XElement(defaultNS + "Attribute"));
                newNamespaceUri.Name = "NamespaceUri";
                newNamespaceUri.RecreateAttributeInstance(namespaceUriRoot);
                att.Attribute.Insert(newNamespaceUri);

                att.AttributeDataType = "";
                AttributeType n = new AttributeType(new System.Xml.Linq.XElement(defaultNS + "Attribute"));
                n.Name = "Name";
                n.AttributeDataType = "xs:string";
                att.Attribute.Insert(n, false);
            }
            else if (nodeId == GuidNodeId)
            {
                att.AttributeDataType = "xs:string";
            }

        }

        private void FillSubAttributes(ref AttributeTypeType att, NodeId nodeId)
        {
            
                ProcessOptionSets(ref att, nodeId);
                ProcessEnumerations(ref att, nodeId);
                RecurseStructures(ref att, nodeId);
                

        }

        private void MakeBuiltInType(ref AttributeType a)
        {
            a.AttributeDataType = "xs:string";
            a.RefAttributeType = ATLPrefix + MetaModelName + "/BuiltInType";
            a.Constraint.Insert(BuiltInTypeConstraint());
        }


        private void MakeAttributeId(ref AttributeType a)
        {
            a.AttributeDataType = "xs:string";
            a.RefAttributeType = ATLPrefix + MetaModelName + "/AttributeId";
            a.Constraint.Insert(AttributeIdConstraint());
        }


        private void ProcessRelativePathElement(ref AttributeTypeType att )
        {
            // custom build the RelativePathElement to use ExplicitNodeId instead of NodeId
            var added2 = new AttributeType(new System.Xml.Linq.XElement(defaultNS + "Attribute"));
            added2.Name = "ReferenceTypeId";
            string path = BuildLibraryReference(ATLPrefix, MetaModelName, "ExplicitNodeId");
            var ob = m_cAEXDocument.FindByPath(path);
            var at = ob as AttributeFamilyType;
            added2.RecreateAttributeInstance(at);
            att.Attribute.Insert(added2, false);

        }



        private void RecurseStructures(ref AttributeTypeType att, NodeId nodeId)
        {
            if( nodeId == RelativePathElementNodeId)
            {
                ProcessRelativePathElement(ref att);
            }
            if (m_modelManager.IsTypeOf(nodeId, structureNode.DecodedNodeId))
            {
                bool debugMessage = false;
                att.AttributeDataType = "";
                var MyNode = FindNode<UANode>(nodeId) as NodeSet.UADataType;
                if (MyNode.Definition != null && MyNode.Definition.Field != null)
                {
                    if (MyNode.Definition.IsOptionSet == true && m_modelManager.IsTypeOf(nodeId, OptionSetStructureNodeId) == true)
                    {
                        AttributeType ValueField = new AttributeType(new System.Xml.Linq.XElement(defaultNS + "Attribute"));
                        ValueField.Name = "Value";
                        var attValue = att.Attribute.Insert(ValueField, false);
                        AttributeType ValidBitsField = new AttributeType(new System.Xml.Linq.XElement(defaultNS + "Attribute"));
                        ValidBitsField.Name = "ValidBits";
                        var attValidBits = att.Attribute.Insert(ValidBitsField, false);
                        for (int i = 0; i < MyNode.Definition.Field.Length; i++)
                        {
                            AttributeType a = new AttributeType(new System.Xml.Linq.XElement(defaultNS + "Attribute"));
                            a.Name = MyNode.Definition.Field[i].Name;
                            a.AttributeDataType = "xs:boolean";
                            attValue.Attribute.Insert(a, false);
                            attValidBits.Attribute.Insert(a, false);
                        }
                    }
                    else
                    {
                        for (int i = 0; i < MyNode.Definition.Field.Length; i++)
                        {
                            AttributeType a = new AttributeType(new System.Xml.Linq.XElement(defaultNS + "Attribute"));
                            a.Name = MyNode.Definition.Field[i].Name;
                            if (att.Attribute[a.Name] == null)  // don't add the attribute if it already exists
                            {
                                a.RefAttributeType = BaseRefFromNodeId(MyNode.Definition.Field[i].DecodedDataType, ATLPrefix, false, 
                                    MyNode.Definition.Field[i].ValueRank >= ValueRanks.OneDimension );

                                a.AttributeDataType = GetAttributeDataType(MyNode.Definition.Field[i].DecodedDataType);
                                if (nodeId.NamespaceIndex == 0)
                                {
                                    if (a.Name == "BuiltInType")
                                        MakeBuiltInType(ref a);
                                    else if (a.Name == "AttributeId")
                                        MakeAttributeId(ref a);
                                }

                                if (MyNode.Definition.Field[i].ValueRank >= ValueRanks.OneDimension) // insert the first element in the list as a placeholder
                                {
                                    AttributeType aa = new AttributeType(new System.Xml.Linq.XElement(defaultNS + "Attribute"));
                                    aa.RefAttributeType = BaseRefFromNodeId(MyNode.Definition.Field[i].DecodedDataType, ATLPrefix, false, false);
                                    aa.AttributeDataType = GetAttributeDataType(MyNode.Definition.Field[i].DecodedDataType);
                                    var BaseNode = m_modelManager.FindNode<UANode>(MyNode.Definition.Field[i].DecodedDataType);
                                    if (BaseNode != null)
                                        aa.Name = BaseNode.DecodedBrowseName.Name;
                                    else
                                        aa.Name = "0";
                                    var b = aa as AttributeTypeType;
                                    FillSubAttributes(ref b, MyNode.Definition.Field[i].DecodedDataType);
                                    a.Attribute.Insert(aa, false);
                                }
                                else
                                {
                                    var b = a as AttributeTypeType;
                                    FillSubAttributes(ref b, MyNode.Definition.Field[i].DecodedDataType);
                                }
                                att.Attribute.Insert(a, false);
                            }
                        }
                    }
                }
            }
        }

        private void AddAttributeData( AttributeFamilyType attribute, UANode uaNode )
        {
            AddStructureFieldDefinition( attribute, uaNode );

            AddEnumerationFieldDefinition( attribute, uaNode );

            AddOptionSetFieldDefinition( attribute, uaNode );

            AttributeType nodeIdAttribute = AddModifyAttribute( attribute.Attribute,"NodeId", "NodeId", 
                new Variant( uaNode.DecodedNodeId ) );

            MinimizeNodeId( nodeIdAttribute );

            UADataType dataType = uaNode as UADataType;
            if (dataType != null && dataType.IsAbstract)
            {
                AttributeType isAbstractAttribute = AddModifyAttribute(
                    attribute.Attribute, "IsAbstract", "Boolean", true);
                isAbstractAttribute.AdditionalInformation.Append(OpcUaTypeOnly);
                RemoveUnwantedAttribute(isAbstractAttribute, "NodeId");
            }

            nodeIdAttribute.AdditionalInformation.Append( OpcUaTypeOnly );
        }


        private void AddStructureFieldDefinition( AttributeFamilyType attribute, UANode uaNode )
        {
            if ( m_modelManager.IsTypeOf( uaNode.DecodedNodeId, structureNode.DecodedNodeId ) )
            {
                attribute.AttributeDataType = "";
                NodeSet.UADataType uaDataType = uaNode as NodeSet.UADataType;

                if( uaDataType != null && 
                    uaDataType.Definition != null && 
                    uaDataType.Definition.Field != null )
                {
                    if( !uaDataType.Definition.IsOptionSet || 
                        !m_modelManager.IsTypeOf( uaNode.DecodedNodeId, OptionSetStructureNodeId ) )
                    {
                        string path = BuildLibraryReference( ATLPrefix, Opc.Ua.Namespaces.OpcUa, "StructureField" );

                        for( int index = 0; index < uaDataType.Definition.Field.Length; index++ )
                        {
                            DataTypeField field = uaDataType.Definition.Field[ index ];
                            AttributeTypeType fieldDefinitionAttribute = attribute.Attribute[ field.Name ] ;
                            if( fieldDefinitionAttribute != null )
                            {
                                AttributeType structureFieldAttribute =
                                    fieldDefinitionAttribute.Attribute[ "StructureFieldDefinition" ];

                                if( structureFieldAttribute == null )
                                {
                                    AttributeFamilyType structureFieldDefinition = m_cAEXDocument.FindByPath( path ) as AttributeFamilyType;

                                    structureFieldAttribute = new AttributeType(
                                        new System.Xml.Linq.XElement( defaultNS + "Attribute" ) );

                                    RecreateAttributeInstance( structureFieldDefinition , structureFieldAttribute);
                                    structureFieldAttribute.Name = "StructureFieldDefinition";
                                    structureFieldAttribute.AdditionalInformation.Append( OpcUaTypeOnly );


                                    // Now fill the data
                                    RemoveUnwantedAttribute(structureFieldAttribute, "Name");

                                    if ( field.ValueRank == ValueRanks.Scalar || 
                                        field.ValueRank >= ValueRanks.OneDimension )
                                    {
                                        AddModifyAttribute(structureFieldAttribute.Attribute,
                                            "ValueRank", "Int32", new Variant(field.ValueRank));
                                    }
                                    else
                                    {
                                        RemoveUnwantedAttribute( structureFieldAttribute, "ValueRank" );
                                    }

                                    if ( field.IsOptional )
                                    {
                                        AddModifyAttribute(structureFieldAttribute.Attribute,
                                            "IsOptional", "Boolean", new Variant(true));
                                    }
                                    else
                                    {
                                        RemoveUnwantedAttribute(structureFieldAttribute, "IsOptional");
                                    }

                                    if ( field.ValueRank >= ValueRanks.OneDimension &&
                                        !string.IsNullOrEmpty( field.ArrayDimensions ) )
                                    {
                                        SetArrayDimensions(structureFieldAttribute.Attribute, field.ArrayDimensions);
                                        RemoveUnwantedAttribute(structureFieldAttribute.Attribute["ArrayDimensions"], 
                                            "StructureFieldDefinition");
                                    }
                                    else
                                    {
                                        RemoveUnwantedAttribute(structureFieldAttribute, "ArrayDimensions");
                                    }


                                    // Max String Length is only for strings and bytestrings
                                    // This seems to be a point for discussion.
                                    // Do we put max string length in if it zero?
                                    if ( field.MaxStringLength > 0 && 
                                        ( m_modelManager.IsTypeOf( field.DecodedDataType, Opc.Ua.DataTypeIds.String ) ||
                                          m_modelManager.IsTypeOf( field.DecodedDataType, Opc.Ua.DataTypeIds.ByteString ) ) )
                                    {
                                        AddModifyAttribute( structureFieldAttribute.Attribute,
                                            "MaxStringLength", "UInt32", new Variant( field.MaxStringLength ) );
                                    }
                                    else if ( structureFieldAttribute.Attribute[ "MaxStringLength" ] != null )
                                    {
                                        RemoveUnwantedAttribute( structureFieldAttribute, "MaxStringLength" );
                                    }

                                    AttributeType descriptionCreated = AddLocalizedTextArrayAttribute(
                                        structureFieldAttribute,
                                        "Description",
                                        field.Description);

                                    if (descriptionCreated != null)
                                    {
                                        RemoveUnwantedAttribute(descriptionCreated,"StructureFieldDefinition");
                                    }

                                    if ( field.AllowSubTypes)
                                    {
                                        AddModifyAttribute(structureFieldAttribute.Attribute,
                                            "AllowSubTypes", "Boolean", new Variant(true));
                                    }

                                    // Remove the NodeId from the structure Field
                                    AttributeType nodeIdAttribute = structureFieldAttribute.Attribute[ "DataType" ];
                                    if( nodeIdAttribute != null )
                                    {
                                        structureFieldAttribute.Attribute.RemoveElement( nodeIdAttribute );
                                    }

                                    RemoveUnwantedNodeIdAttribute(structureFieldAttribute);
                                    RemoveNodeIdsFromDefinition(structureFieldAttribute);

                                    if (structureFieldAttribute.Attribute.Count > 0)
                                    {
                                        fieldDefinitionAttribute.Attribute.Insert(structureFieldAttribute);
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

        private void AddEnumerationFieldDefinition(AttributeFamilyType attribute, UANode uaNode)
        {
            UADataType enumNode = uaNode as UADataType;
            if (enumNode != null &&
                enumNode.Definition != null &&
                enumNode.Definition.Field != null &&
                enumNode.Definition.Field.Length > 0)
            {
                string enumPath = BuildLibraryReference(ATLPrefix, Opc.Ua.Namespaces.OpcUa, Enumeration);

                if (attribute.RefBaseClassPath.Equals(enumPath))
                {
                    NodeId enumStringsNodeId = m_modelManager.FindFirstTarget(uaNode.DecodedNodeId,
                        HasPropertyNodeId, true, "EnumStrings");
                    if (enumStringsNodeId != null)
                    {
                        UAVariable enumStrings = FindNode<UAVariable>(enumStringsNodeId);
                        AttributeType added = AddModifyAttribute(attribute.Attribute, "EnumStrings", "LocalizedText",
                            enumStrings.DecodedValue, bListOf: true);
                        if (added != null)
                        {
                            added.AdditionalInformation.Append(OpcUaTypeOnly);
                            RemoveNodeIdsFromDefinition(added);

                            AttributeType nodeIdAttribute = AddModifyAttribute(added.Attribute,
                                "NodeId", "NodeId", new Variant(enumStringsNodeId));
                        }
                    }
                    else
                    {
                        NodeId EnumValuesPropertyId = m_modelManager.FindFirstTarget(uaNode.DecodedNodeId, HasPropertyNodeId, true, "EnumValues");
                        if (EnumValuesPropertyId != null)
                        {
                            UAVariable enumValues = FindNode<UAVariable>(EnumValuesPropertyId);
                            AttributeType added = AddModifyAttribute(attribute.Attribute, "EnumValues", "EnumValueType",
                                enumValues.DecodedValue, bListOf: true);
                            if (added != null)
                            {
                                // Remove empty values
                                RemoveNodeIdsFromDefinition(added);

                                foreach( AttributeType arrayElement in added.Attribute )
                                {
                                    List<string> toBeRemoved = new List<string>();
                                    
                                    foreach ( AttributeType parameter in arrayElement.Attribute )
                                    {
                                        if ( string.IsNullOrEmpty( parameter.Value ) )
                                        {
                                            toBeRemoved.Add(parameter.Name);
                                        }
                                    }

                                    foreach (string removeAttribute in toBeRemoved)
                                    {
                                        RemoveUnwantedAttribute(arrayElement, removeAttribute);
                                    }
                                }

                                AttributeType nodeIdAttribute = AddModifyAttribute(added.Attribute,
                                    "NodeId", "NodeId", new Variant(EnumValuesPropertyId));

                                added.AdditionalInformation.Append(OpcUaTypeOnly);
                            }
                        }
                    }

                    string path = BuildLibraryReference(ATLPrefix, Opc.Ua.Namespaces.OpcUa, "ListOfEnumField");
                    AttributeFamilyType enumFieldDefinition = m_cAEXDocument.FindByPath(path) as AttributeFamilyType;

                    AttributeType enumFields = new AttributeType(
                        new System.Xml.Linq.XElement(defaultNS + "Attribute"));

                    enumFields.RecreateAttributeInstance(enumFieldDefinition as AttributeFamilyType);
                    enumFields.Name = "EnumFieldDefinition";
                    enumFields.AdditionalInformation.Append(OpcUaTypeOnly);

                    string enumFieldPath = BuildLibraryReference(ATLPrefix, Opc.Ua.Namespaces.OpcUa, "EnumField");
                    AttributeFamilyType enumFieldSource = m_cAEXDocument.FindByPath(enumFieldPath) as AttributeFamilyType;

                    foreach (DataTypeField fieldDefinition in enumNode.Definition.Field)
                    {
                        AttributeType fieldAttribute = new AttributeType(new System.Xml.Linq.XElement(defaultNS + "Attribute"));

                        RecreateAttributeInstance(enumFieldSource, fieldAttribute);
                        fieldAttribute.Name = fieldDefinition.Name;

                        // Remove the Name
                        RemoveUnwantedAttribute(fieldAttribute, "Name");

                        // Description is an array of LocalizedText

                        AddLocalizedTextArrayAttribute(fieldAttribute, "Description", fieldDefinition.Description);

                        AddLocalizedTextArrayAttribute( fieldAttribute, "DisplayName", fieldDefinition.DisplayName);

                        AddModifyAttribute(fieldAttribute.Attribute,
                            "Value", "Int32", new Variant(fieldDefinition.Value));

                        RemoveNodeIdsFromDefinition(fieldAttribute);

                        enumFields.Attribute.Insert(fieldAttribute, false, true);
                    }
                    RemoveNodeIdsFromDefinition(enumFields);

                    attribute.Attribute.Insert(enumFields, false, true);
                }
            }
        }

        private void AddOptionSetFieldDefinition( AttributeFamilyType attribute, UANode uaNode )
        {
            UADataType optionSetNode = uaNode as UADataType;
            if (optionSetNode != null &&
                optionSetNode.Definition != null &&
                optionSetNode.Definition.IsOptionSet == true)
            {
                if ( optionSetNode.Definition.Field != null &&
                    optionSetNode.Definition.Field.Length > 0 )
                {
                    string path = BuildLibraryReference(ATLPrefix, Opc.Ua.Namespaces.OpcUa, "ListOfOptionSet");
                    AttributeFamilyType optionSetFieldDefinition = m_cAEXDocument.FindByPath(path) as AttributeFamilyType;

                    AttributeType optionSetFields = new AttributeType(
                        new System.Xml.Linq.XElement(defaultNS + "Attribute"));

                    optionSetFields.RecreateAttributeInstance(optionSetFieldDefinition);
                    optionSetFields.Name = "OptionSetFieldDefinition";
                    optionSetFields.AdditionalInformation.Append(OpcUaTypeOnly);

                    string optionSetPath = BuildLibraryReference(ATLPrefix, Opc.Ua.Namespaces.OpcUa, "OptionSet");
                    AttributeFamilyType optionSetSource = m_cAEXDocument.FindByPath(optionSetPath) as AttributeFamilyType;

                    foreach (DataTypeField fieldDefinition in optionSetNode.Definition.Field)
                    {
                        AttributeType fieldAttribute = new AttributeType(new System.Xml.Linq.XElement(defaultNS + "Attribute"));

                        fieldAttribute.RecreateAttributeInstance(optionSetSource);
                        fieldAttribute.Name = fieldDefinition.Name;

                        AttributeType valueAttribute = AddModifyAttribute(fieldAttribute.Attribute,
                            "Value", "Int32", new Variant(fieldDefinition.Value));

                        RemoveUnwantedAttribute(valueAttribute, "NodeId");
                        RemoveUnwantedAttribute(fieldAttribute, "IsAbstract");
                        RemoveUnwantedAttribute(fieldAttribute, "ValidBits");
                        RemoveUnwantedAttribute(fieldAttribute, "NodeId");

                        optionSetFields.Attribute.Insert(fieldAttribute, false, true);
                    }

                    attribute.Attribute.Insert(optionSetFields, false, true);

                    NodeId optionSetsPropertyId = m_modelManager.FindFirstTarget(
                        optionSetNode.DecodedNodeId, HasPropertyNodeId, true, "OptionSetValues");

                    if (optionSetsPropertyId != null && m_modelManager.IsTypeOf(optionSetNode.DecodedNodeId, NumberNodeId))
                    {
                        UANode optionSetsPropertyNode = FindNode<UANode>(optionSetsPropertyId);
                        UAVariable optionSets = optionSetsPropertyNode as UAVariable;

                        Opc.Ua.LocalizedText[] optionSetValues = optionSets.DecodedValue.Value as Opc.Ua.LocalizedText[];
                        AttributeType optionSetValuesAttribute = AddModifyAttribute(attribute.Attribute, "OptionSetValues",
                            Opc.Ua.DataTypes.LocalizedText, new Variant(optionSetValues), bListOf: true);
                        optionSetValuesAttribute.AdditionalInformation.Append(OpcUaTypeOnly);
                        RemoveNodeIdsFromDefinition(optionSetValuesAttribute);
                        AddModifyAttribute(optionSetValuesAttribute.Attribute, "NodeId", "NodeId",
                            new Variant(optionSetsPropertyId));
                    }
                }
                else
                {
                    bool unexpected = true;
                }
            }
        }


        private AttributeFamilyType ProcessDataType(NodeSet.UANode node)
        {
            var typeNode = node as MarkdownProcessor.NodeSet.UADataType;

            if (typeNode.Purpose != MarkdownProcessor.NodeSet.DataTypePurpose.Normal)
                return null;
            if (ExcludedDataTypeList.Contains(typeNode.DecodedBrowseName.Name))
                return null;

            var added = new AttributeFamilyType(new System.Xml.Linq.XElement(defaultNS + "AttributeType"));
            var att = added as AttributeTypeType;
            added.Name = node.DecodedBrowseName.Name;
            added.ID = AmlIDFromNodeId(node.DecodedNodeId);

            m_atl_temp.AttributeType.Insert(added);

            added.AttributeDataType = GetAttributeDataType(node.DecodedNodeId);

            NodeId BaseNodeId = m_modelManager.FindFirstTarget(node.DecodedNodeId, HasSubTypeNodeId, false);
            if (BaseNodeId != null)
            {
                string s = BaseRefFromNodeId(BaseNodeId, ATLPrefix);
                added.RefAttributeType = s;
            }

            // Enumerations
            ProcessEnumerations(ref att, node.DecodedNodeId);
            // OptionSets
            ProcessOptionSets(ref att, node.DecodedNodeId);
            // structures     
            RecurseStructures(ref att, node.DecodedNodeId);
       

            return added;
        }
        
        private AttributeType AddLocalizedTextArrayAttribute( AttributeType attribute, 
            string name, NodeSet.LocalizedText[] texts)
        {
            AttributeType created = null;

            // Currently for Field Definitions
            Variant localizedTextArray = LocalizedTextArrayAsVariant(texts);
            if (localizedTextArray.TypeInfo != null &&
                localizedTextArray.TypeInfo.BuiltInType != null &&
                localizedTextArray.TypeInfo.BuiltInType == BuiltInType.Variant)
            {
                created = AddModifyAttribute(attribute.Attribute, name, "LocalizedText", 
                    localizedTextArray, bListOf: true);
            }
            else
            {
                RemoveUnwantedAttribute(attribute, name);
            }

            return created;
        }

        private Variant LocalizedTextArrayAsVariant(NodeSet.LocalizedText[] array )
        {
            Variant localizedTextArray = new Variant();

            if (array != null && array.Length >= 0)
            {
                List<Variant> localizedTextList = new List<Variant>(array.Length);
                foreach (NodeSet.LocalizedText text in array)
                {
                    localizedTextList.Add(
                        new Variant(
                            new LocalizedText(text.Locale, text.Value)));
                }

                localizedTextArray = new Variant(localizedTextList);
            }

            return localizedTextArray;
        }

        #endregion

        #region INSTANCE

        private void CreateInstances()
        {
            // add an InstanceHierarchy to the ROOT CAEXFile element
            InstanceHierarchyType myIH = m_cAEXDocument.CAEXFile.New_InstanceHierarchy("OPC UA Instance Hierarchy");
            AddLibraryHeaderInfo(myIH);

            UANode RootNode = FindNode<UANode>(RootNodeId);

            Dictionary<NodeId, List<ReferenceInfo>> instanceReferences = 
                new Dictionary<NodeId, List<ReferenceInfo>>();

            // Issue 143.  Need to go through and find multiple Target NodeIds, and respect 
            // those in the instance creation routine
            BuildInstanceReferences(instanceReferences, RootNode);
            MinimizeInstanceReferences(instanceReferences);

            RecursiveAddModifyInstance<InstanceHierarchyType>(ref myIH, RootNode, false);
        }

        private void BuildInstanceReferences(Dictionary<NodeId, List<ReferenceInfo>> instanceReferences, 
            UANode toAdd )
        {
            List<ReferenceInfo> references = m_modelManager.FindReferences(toAdd.DecodedNodeId);

            foreach (ReferenceInfo reference in references)
            {
                if (reference.IsForward == true)
                {
                    if (m_modelManager.IsTypeOf(reference.ReferenceTypeId, HierarchicalNodeId) == true)
                    {
                        UANode targetNode = FindNode<UANode>(reference.TargetId);
                        if (reference.TargetId != TypesFolderNodeId)
                        {
                            if (!instanceReferences.ContainsKey(reference.TargetId))
                            {
                                instanceReferences[reference.TargetId] = new List<ReferenceInfo>();
                            }

                            instanceReferences[reference.TargetId].Add(reference);

                            BuildInstanceReferences(instanceReferences, targetNode);
                        }
                    }
                }
            }
        }

        private void MinimizeInstanceReferences(Dictionary<NodeId, List<ReferenceInfo>> instanceReferences )
        {
            foreach (KeyValuePair<NodeId, List<ReferenceInfo>> pair in instanceReferences)
            {
                if (pair.Value.Count > 1)
                {
                    m_multipleReferences.Add(pair.Key, pair.Value);
                }
            }
        }

        InternalElementType RecursiveAddModifyInstance<T>(ref T parent, UANode toAdd, bool serverDiagnostics) where T : IInternalElementContainer
        {
            string amlId = AmlIDFromNodeId(toAdd.DecodedNodeId);
            string prefix = toAdd.DecodedBrowseName.Name;

            string decodedNodeId = toAdd.DecodedNodeId.ToString();

            Utils.LogTrace( "Add Instance {0} [{1}] Start", prefix, decodedNodeId );

            //first see if node already exists
            InternalElementType ie = parent.InternalElement[toAdd.DecodedBrowseName.Name];
            if (ie == null)
            {
                SystemUnitFamilyType suc;
                
                var TypeDefNodeId = m_modelManager.FindFirstTarget(toAdd.DecodedNodeId, HasTypeDefinitionNodeId, true);
                var path = BaseRefFromNodeId(TypeDefNodeId, SUCPrefix);

                suc = m_cAEXDocument.FindByPath(path) as SystemUnitFamilyType;

                if (suc == null && toAdd.NodeClass == NodeClass.Method)
                {
                    suc = m_cAEXDocument.FindByPath( BuildLibraryReference( SUCPrefix, MetaModelName, MethodNodeClass)) as SystemUnitFamilyType;
                    Debug.Assert(suc != null);
                    // AddModifyAttribute(suc.Attribute, "Executable", "Boolean", true);  // #7 removed Executable an UserExecutable
                    // AddModifyAttribute(suc.Attribute, "UserExecutable", "Boolean", true);  // #7 removed Executable an UserExecutable
                }
                Debug.Assert(suc != null);

                InternalElementType existingInternalElement = FindInstanceInternalElement(toAdd);
                if (existingInternalElement != null)
                {
                    return existingInternalElement;
                }

                if ( prefix.StartsWith("http://"))
                {
                    prefix = WebUtility.UrlEncode(toAdd.DecodedBrowseName.Name);
                }

                ie = CreateClassInstanceWithIDReplacement(prefix + "_", suc);
                
                parent.Insert(ie);
                m_instances.Add( toAdd.DecodedNodeId, ie);
            }
            else
            {
                if (ie.ID != amlId)
                {
                    InternalElementType existingInternalElement = FindInstanceInternalElement(toAdd);
                    if (existingInternalElement != null)
                    {
                        parent.InternalElement.RemoveElement(ie);   
                        ie = existingInternalElement;
                    }
                }
            }
            Debug.Assert(ie != null);

            // Just because the ie (InternalElement) was found in the parent does not mean that the ID was correctly set.
            // Set/reset the Id even though it might already be done correctly.
            ie.ID = amlId;
            ie.Name = toAdd.DecodedBrowseName.Name;
            AddBaseNodeClassAttributes(ie.Attribute, toAdd);

            RemoveUnwantedAttribute(ie.Attribute, "IsAbstract");

            // set the values to match the values in the nodeset
            if (toAdd.NodeClass == NodeClass.Variable)
            {
                //  Set the datatype for Value
                var varnode = toAdd as NodeSet.UAVariable;
                bool bListOf = ( varnode.ValueRank >= ValueRanks.OneDimension ); // use ListOf when its a UA array

                Variant variant = varnode.DecodedValue;

                if( serverDiagnostics && 
                    !toAdd.DecodedNodeId.Equals( Opc.Ua.VariableIds.Server_ServerDiagnostics_EnabledFlag ) )
                {
                    // Don't set this value
                    variant = new Variant();
                    Debug.WriteLine( "Server Diagnostics and " + toAdd.BrowseName + " [" + toAdd.NodeId + "]" );
                }

                AddModifyAttribute( ie.Attribute, "Value", varnode.DecodedDataType, variant, bListOf);
                var DataTypeNode = FindNode<UANode>( varnode.DecodedDataType );
                var sUADataType = DataTypeNode.DecodedBrowseName.Name;

                AddModifyAttribute( ie.Attribute, "ValueRank", "Int32", varnode.ValueRank );
                SetArrayDimensions( ie, varnode.ArrayDimensions );

                if (varnode.MinimumSamplingInterval > 0)
                {
                    AddModifyAttribute(ie.Attribute, "MinimumSamplingInterval", "Double", varnode.MinimumSamplingInterval);
                }
            }

            if( toAdd.DecodedNodeId.Equals( Opc.Ua.ObjectIds.Server_ServerDiagnostics ) )
            {
                serverDiagnostics = true;
            }

            // TODO set the values of the other attributes to match the instance ??

            // follow forward hierarchical references to add the children

            // now add the references and contained objects
            var nodeId = toAdd.DecodedNodeId;
            var refList = m_modelManager.FindReferences(nodeId);
            if (refList != null)
            {
                HashSet<string> foundInternalElements = new HashSet<string>();
                foreach (var reference in refList)
                {
                    if (reference.IsForward == true)
                    {
                        if (m_modelManager.IsTypeOf(reference.ReferenceTypeId, HierarchicalNodeId) == true )
                        {
                            string refURI = m_modelManager.FindModelUri(reference.ReferenceTypeId);
                            var ReferenceTypeNode = FindNode<UANode>(reference.ReferenceTypeId);
                            SystemUnitClassType ie_systemUnitClassType = ie as SystemUnitClassType;
                            var sourceInterface = FindOrAddInterface(ref ie_systemUnitClassType, refURI, 
                                ReferenceTypeNode.DecodedBrowseName.Name, nodeId);
                            var targetNode = FindNode<UANode>(reference.TargetId);
                           
                            if (reference.TargetId != TypesFolderNodeId)
                            {
                                var childIE = RecursiveAddModifyInstance<InternalElementType>(ref ie, targetNode, 
                                    serverDiagnostics);
                                foundInternalElements.Add(childIE.Name);

                                SystemUnitClassType childSystemUnitClass = childIE as SystemUnitClassType;
                                var destInterface = FindOrAddInterface(ref childSystemUnitClass, refURI, 
                                    ReferenceTypeNode.DecodedBrowseName.Name + "]/[" + sourceInterface.Attribute["InverseName"].Value, 
                                    targetNode.DecodedNodeId);

                                if ( m_multipleReferences.TryGetValue(targetNode.DecodedNodeId, 
                                    out List<ReferenceInfo> multipleReferences))
                                {
                                    foreach (ReferenceInfo saveReference in multipleReferences)
                                    {
                                        if ( saveReference.Equals(reference))
                                        {
                                            OverrideBooleanAttribute(destInterface.Attribute,
                                                KeepExternalInterfaceAttribute, 
                                                value: true, typeOnly: true);
                                            break;
                                        }
                                    }
                                }

                                FindOrAddInternalLink(ref ie, sourceInterface.ID, destInterface.ID, targetNode.DecodedBrowseName.Name);
                            }
                        }
                        else if (m_modelManager.IsTypeOf(reference.ReferenceTypeId, Opc.Ua.ReferenceTypeIds.NonHierarchicalReferences) == true)
                        {
                            _nonHierarchicalReferences.AddReference(reference, instance: _runningInstances);
                        }
                    }
                }

                // Are there internal Elements that are not handled by the references?
                // Should they be removed?
                List<InternalElementType> internalElementsToRemove = new List<InternalElementType>();
                foreach (InternalElementType internalElement in ie.InternalElement)
                {
                    if (!foundInternalElements.Contains(internalElement.Name))
                    {
                        internalElementsToRemove.Add(internalElement);
                    }
                }

                foreach (InternalElementType internalElement in internalElementsToRemove)
                {
                    InternalLinkType link = ie.InternalLink[internalElement.Name];
                    if (link != null)
                    {
                        ExternalInterfaceType externalInterface = null;
                        foreach (ExternalInterfaceType external in internalElement.ExternalInterface)
                        {
                            if (external.ID.Equals(link.RefPartnerSideB))
                            {
                                externalInterface = external;
                                break;
                            }
                        }

                        if (externalInterface != null)
                        {
                            bool delete = true;
                            foreach (ExternalInterfaceType external in internalElement.ExternalInterface)
                            {
                                if (!external.Equals(externalInterface))
                                {
                                    if (external.ID.Equals(link.RefPartnerSideA))
                                    {
                                        delete = false;
                                        break;
                                    }
                                }
                            }

                            if (delete)
                            {
                                ie.ExternalInterface.RemoveElement(externalInterface);
                            }
                        }
                        ie.InternalLink.RemoveElement(link);
                        ie.InternalElement.RemoveElement(internalElement);
                    }
                }
            }

            RebuildExternalInterfaces(prefix + "_", ie);

            CompareLinksToExternaInterfaces(ie, ie);

            Utils.LogTrace( "Add Instance {0} [{1}] Complete", prefix, decodedNodeId );

            return ie;
        }

        InternalElementType FindInstanceInternalElement(UANode node)
        {
            InternalElementType internalElement = null;

            /*
             * Previously, this was accomplished by using the document.FindById method.
             * However due to Issue 143 - a duplication issue, this added a substantial performance hit
             * Adding a LookupService actual increments the performance hit to an unusable level.
             * Adding a ServiceLocater.QueryService and using ILookupUpTable to load the table does 
             * improve the performance to an acceptable level, but it is optimized for ReadOnly document
             * access.  This converter needs Read/Write access, which makes using the ILookUpTable not feasible.
             * A local dictionary is used instead.
             */

            if (m_instances.TryGetValue(node.DecodedNodeId, out internalElement))
            {
                Utils.LogTrace("Add Instance {0} [{1}] Previously Added", 
                    node.DecodedBrowseName.Name, node.DecodedNodeId);
            }

            return internalElement;
        }

        // add an internal link if it does not already exist
        InternalLinkType FindOrAddInternalLink(ref InternalElementType ie, string sourceID, string destinationID, string linkName)        
        {
            foreach( var link in ie.InternalLink)
            {
                if (link.RefPartnerSideA == sourceID && link.RefPartnerSideB == destinationID)
                    return link;  // link already exists
                if (link.Name == linkName)
                {
                    ie.InternalLink.RemoveElement(link);
                }
            }
            // add a new link
            var internalLink = ie.New_InternalLink(linkName);
            internalLink.RefPartnerSideA = sourceID;
            internalLink.RefPartnerSideB = destinationID;
            return internalLink;
        }

        #endregion

        #region Complex

        private XmlDecoder CreateDecoder( XmlElement source )
        {
            ServiceMessageContext messageContext = new ServiceMessageContext();
            messageContext.NamespaceUris = m_modelManager.NamespaceUris;
            messageContext.ServerUris = m_modelManager.ServerUris;

            XmlDecoder decoder = new XmlDecoder( source, messageContext );

            return decoder;
        }

        private XmlElement SearchForElement( string name, XmlElement source )
        {
            XmlElement element = source[ name ];
            if( element == null )
            {
                string uaxId = "uax:";
                if( !name.StartsWith( uaxId, StringComparison.OrdinalIgnoreCase ) )
                {
                    element = source[ uaxId + name ];
                }
            }

            return element;
        }

        private Variant CreateComplexVariant( string name, DataTypeField typeDefinition, XmlElement source )
        {
            Variant variant = new Variant();

            XmlElement xmlElement = SearchForElement( name, source );

            if (xmlElement == null)
            {
                XmlElement body = SearchForElement("Body", source);
                XmlElement type = SearchForElement("TypeId", source);

                if (body != null && type != null)
                {
                    if (body.ChildNodes.Count == 1)
                    {
                        XmlNodeList list = body.ChildNodes;
                        XmlElement subBody = list.Item(0) as XmlElement;
                        if (subBody != null)
                        {
                            xmlElement = SearchForElement(name, subBody);
                        }
                    }
                }
            }

            if ( xmlElement != null )
            {
                BuiltInType baseBuiltInType = BuiltInType.Null;

                NodeId baseBuiltInTypeNodeId = ComplexGetBuiltInTypeEx( typeDefinition.DecodedDataType );
                if( baseBuiltInTypeNodeId != null )
                {
                    baseBuiltInType = ComplexGetBuiltInType( baseBuiltInTypeNodeId );
                }

                if(typeDefinition.DecodedDataType.Equals( Opc.Ua.DataTypeIds.BaseDataType ) )
                {
                    // Defined as variant, or nothing at all
                    baseBuiltInType = BuiltInType.Variant;
                    baseBuiltInTypeNodeId = typeDefinition.DecodedDataType;
                }

                if( baseBuiltInType == BuiltInType.Null )
                {
                    Utils.LogError( "CreateComplexVariant Unable to get builtInType for " + name + " [" + typeDefinition.DecodedDataType + "]" );
                }
                else
                {
                    UANode baseBuiltInTypeNode = m_modelManager.FindNode<UANode>( baseBuiltInTypeNodeId );

                    string baseBuiltInTypeName = baseBuiltInTypeNode.BrowseName;
                    if( typeDefinition.ValueRank >= ValueRanks.OneDimension )
                    {
                        baseBuiltInTypeName = "ListOf" + baseBuiltInTypeNode.BrowseName;
                    }

                    XmlDocument document = new XmlDocument();

                    switch( baseBuiltInType )
                    {
                        case BuiltInType.Boolean:
                        case BuiltInType.SByte:
                        case BuiltInType.Byte:
                        case BuiltInType.Int16:
                        case BuiltInType.UInt16:
                        case BuiltInType.Int32:
                        case BuiltInType.UInt32:
                        case BuiltInType.Int64:
                        case BuiltInType.UInt64:
                        case BuiltInType.Float:
                        case BuiltInType.Double:
                        case BuiltInType.String:
                        case BuiltInType.DateTime:
                        case BuiltInType.ByteString:
                        case BuiltInType.Guid:
                        case BuiltInType.XmlElement:
                        case BuiltInType.NodeId:
                        case BuiltInType.ExpandedNodeId:
                        case BuiltInType.StatusCode:
                        case BuiltInType.QualifiedName:
                        case BuiltInType.LocalizedText:
                        case BuiltInType.DataValue:
                        case BuiltInType.DiagnosticInfo:
                            {
                                XmlElement complexElement = document.CreateElement( baseBuiltInTypeName, Namespaces.OpcUaXsd );
                                complexElement.InnerXml = xmlElement.InnerXml;

                                XmlDecoder decoder = CreateDecoder( complexElement );
                                Opc.Ua.TypeInfo typeInfo = null;
                                variant = new Variant( decoder.ReadVariantContents( out typeInfo ) );
                                decoder.Close();

                                break;
                            }

                        case BuiltInType.Variant: // BaseDataType
                            {
                                XmlElement valueElement = xmlElement[ "Value" ];
                                if( valueElement != null )
                                {
                                    XmlDocument variantDocument = new XmlDocument();
                                    variantDocument.LoadXml( valueElement.InnerXml );

                                    XmlElement complexElement = variantDocument.DocumentElement;

                                    XmlDecoder decoder = CreateDecoder( complexElement );
                                    Opc.Ua.TypeInfo typeInfo = null;
                                    variant = new Variant( decoder.ReadVariantContents( out typeInfo ) );
                                    decoder.Close();
                                }


                                break;
                            }

                        case BuiltInType.ExtensionObject:
                            {
                                ExpandedNodeId absoluteId = NodeId.ToExpandedNodeId(
                                    typeDefinition.DecodedDataType, m_modelManager.NamespaceUris );

                                if( typeDefinition.ValueRank >= ValueRanks.OneDimension )
                                {
                                    ExpandedNodeId elementId = absoluteId;

                                    List<ExtensionObject> extensions = new List<ExtensionObject>();
                                    foreach( XmlElement arrayElement in xmlElement.ChildNodes )
                                    {
                                        if ( typeDefinition.AllowSubTypes)
                                        {
                                            ExpandedNodeId expandedNodeId = GetExtensionObjectType(arrayElement);
                                            if (expandedNodeId != null)
                                            {
                                                elementId = expandedNodeId;
                                            }
                                        }
                                        extensions.Add( new ExtensionObject( elementId, arrayElement ) );
                                    }

                                    variant = new Variant( extensions );
                                }
                                else
                                {
                                    ExtensionObject extensionObject = new ExtensionObject( absoluteId, xmlElement );
                                    variant = new Variant( extensionObject );
                                }


                                break;
                            }

                        case BuiltInType.Enumeration:
                            {
                                UADataType enumDefinition = m_modelManager.FindNode<UADataType>( typeDefinition.DecodedDataType );

                                if( enumDefinition != null &&
                                    enumDefinition.Definition != null &&
                                    enumDefinition.Definition.Field != null )
                                {
                                    if (typeDefinition.ValueRank >= ValueRanks.OneDimension)
                                    {
                                        // There should be one child node with a list
                                        if ( xmlElement.ChildNodes.Count == 1 )
                                        {
                                            XmlElement listNode = xmlElement.FirstChild as XmlElement;
                                            if ( listNode != null )
                                            {
                                                List<int> values = new List<int>();
                                                foreach (XmlElement child in listNode.ChildNodes)
                                                {
                                                    int enumValue = ParseEnumFromXml(enumDefinition, child);

                                                    if (enumValue >= 0)
                                                    {
                                                        values.Add(enumValue);
                                                    }
                                                }
                                                if (values.Count > 0)
                                                {
                                                    variant = new Variant(values);
                                                }

                                            }
                                            else
                                            {
                                                bool unexpected = true;
                                            }
                                        }
                                        else
                                        {
                                            bool unexpected = true;
                                        }
                                    }
                                    else
                                    {
                                        int enumValue = ParseEnumFromXml(enumDefinition, xmlElement);

                                        if (enumValue >= 0)
                                        {
                                            variant = new Variant(enumValue);
                                        }
                                    }
                                }

                                break;
                            }

                        default:
                            {
                                Utils.LogError( "Unhandled CreateComplexVariant " + source.Name );
                                break;
                            }
                    }
                }
            }

            return variant;
        }

        private int ParseEnumFromXml(UADataType enumDefinition, XmlElement xmlElement )
        {
            int enumValue = -1;

            string value = xmlElement.InnerText;

            int parsedValue = -1;
            bool useInt = int.TryParse(value, out parsedValue);
            if (!useInt)
            {
                if (value.Contains('_'))
                {
                    string[] parts = value.Split('_');
                    if (parts.Length == 2)
                    {
                        useInt = int.TryParse(parts[1], out parsedValue);
                    }
                }
            }

            foreach (DataTypeField dataTypeField in enumDefinition.Definition.Field)
            {
                int createEnumValue = -1;
                if (useInt)
                {
                    if (dataTypeField.Value == parsedValue)
                    {
                        createEnumValue = dataTypeField.Value;
                    }
                }
                else if (dataTypeField.Name.Equals(value, StringComparison.OrdinalIgnoreCase))
                {
                    createEnumValue = dataTypeField.Value;
                }

                if (createEnumValue >= 0)
                {
                    enumValue = createEnumValue;
                    break;
                }
            }

            return enumValue;
        }

        private BuiltInType ComplexGetBuiltInType( NodeId source )
        {
            BuiltInType builtInType = BuiltInType.Null;

            NodeId builtInNodeId = ComplexGetBuiltInTypeEx( source );

            if( builtInNodeId != null )
            {
                if( builtInNodeId.IdType == IdType.Numeric && builtInNodeId.NamespaceIndex == 0 )
                {
                    uint identifier = (UInt32)builtInNodeId.Identifier;
                    builtInType = (BuiltInType)identifier;
                }
            }

            return builtInType;
        }

        public NodeId ComplexGetBuiltInTypeEx( NodeId sourceId )
        {
            // Not happy with Model Manager GetBuiltInType
            List<ReferenceInfo> references = m_modelManager.FindReferences( sourceId );

            if( references != null )
            {
                foreach( var ii in references )
                {
                    if( !ii.IsForward )
                    {
                        if( HasSubTypeNodeId != ii.ReferenceTypeId )
                        {
                            continue;
                        }

                        if( ii.TargetId.Equals( Opc.Ua.DataTypeIds.Integer ) ||
                            ii.TargetId.Equals( Opc.Ua.DataTypeIds.UInteger ) ||
                            ii.TargetId.Equals( Opc.Ua.DataTypeIds.Number ) ||
                            ii.TargetId.Equals( Opc.Ua.DataTypeIds.BaseDataType ) )
                        {
                            return sourceId;
                        }
                        else
                        {
                            return ComplexGetBuiltInTypeEx( ii.TargetId );
                        }
                    }
                }
            }

            return null;
        }

        private ExpandedNodeId GetExtensionObjectType(XmlElement extensionXml)
        {
            ExpandedNodeId expandedTypeNodeId = null;

            NodeId typeNodeId = null;

            XmlElement typeIdXmlElement = SearchForElement("TypeId", extensionXml);
            if (typeIdXmlElement != null)
            {
                XmlElement identifierXmlElement = SearchForElement("Identifier", typeIdXmlElement);
                if (identifierXmlElement != null)
                {
                    typeNodeId = new NodeId(identifierXmlElement.InnerText);
                    if (typeNodeId != null && !typeNodeId.IsNullNodeId)
                    {
                        UANode uaNode = m_modelManager.FindNode<UANode>(typeNodeId);

                        if ( uaNode != null )
                        {
                            UAObject uaObject = uaNode as UAObject;
                            if (uaObject != null)
                            {
                                // Look for encoding
                                List<ReferenceInfo> referenceList = m_modelManager.FindReferences(typeNodeId);
                                foreach (ReferenceInfo referenceInfo in referenceList)
                                {
                                    if (!referenceInfo.IsForward && 
                                        referenceInfo.ReferenceTypeId.Equals(Opc.Ua.ReferenceTypeIds.HasEncoding))
                                    {
                                        typeNodeId = referenceInfo.TargetId;

                                        break;
                                    }
                                }
                            }
                        }
                    }
                }
            }

            if (typeNodeId != null)
            {
                expandedTypeNodeId = NodeId.ToExpandedNodeId(typeNodeId, 
                    m_modelManager.NamespaceUris);
            }

            return expandedTypeNodeId;
        }

        #endregion

        #region Type Only

        private void RemoveTypeOnly()
        {
            RemoveTypeOnlySystemUnitClasses();
            RemoveTypeOnlyInstances();
        }

        private void RemoveTypeOnlyInstances( )
        {
            Utils.LogInfo("Remove TypeOnly Attributes - Instances" );

            foreach( InstanceHierarchyType instanceHierarchy in m_cAEXDocument.CAEXFile.InstanceHierarchy )
            {
                foreach( InternalElementType internalElement in instanceHierarchy.InternalElement )
                {
                    RemoveTypeOnlySystemUnitClassTypes( internalElement );
                }
            }
        }

        private void RemoveTypeOnlySystemUnitClasses( )
        {
            Utils.LogDebug( "Remove TypeOnly Attributes - SystemUnitClasses" );

            foreach( SystemUnitClassLibType libType in m_cAEXDocument.CAEXFile.SystemUnitClassLib )
            {
                foreach( SystemUnitFamilyType familyType in libType.SystemUnitClass )
                {
                    RemoveTypeOnlySystemUnitClassTypes( familyType );
                }
            }
        }

        private void RemoveTypeOnlySystemUnitClassTypes( SystemUnitClassType entity )
        {
            foreach( InternalElementType internalElement in entity.InternalElement )
            {
                RemoveTypeOnlySystemUnitClassTypes( internalElement );
            }

            RemoveTypeOnlyAttributes(entity.Attribute, entity.Name );
            RemoveTypeOnlyExternalInterfaces( entity.ExternalInterface, entity.Name );
        }

        private void RemoveTypeOnlyExternalInterfaces( ExternalInterfaceSequence externalInterfaces, string path )
        {
            foreach( ExternalInterfaceType externalInterface in externalInterfaces )
            {
                RemoveTypeOnlyAttributes( externalInterface.Attribute, path + " " + externalInterface.Name );
            }

            foreach( ExternalInterfaceType externalInterface in externalInterfaces )
            {
                RemoveTypeOnlyExternalInterfaces( externalInterface.ExternalInterface, path + " " + externalInterface.Name );
            }

        }
        private void RemoveTypeOnlyAttributes( AttributeSequence attributes, string path )
        {
            List<AttributeType> attributesToRemove = new List<AttributeType>();

            foreach( AttributeType attribute in attributes )
            {
                if ( attribute.AdditionalInformation != null && 
                    attribute.AdditionalInformation.Count > 0 )
                {
                    foreach( object additionalInformation in attribute.AdditionalInformation )
                    {
                        if ( additionalInformation.GetType().Name == "String" )
                        {
                            string isTypeOnly = additionalInformation as string;
                            if ( !string.IsNullOrEmpty( isTypeOnly ) && 
                                isTypeOnly == OpcUaTypeOnly )
                            {
                                attributesToRemove.Add( attribute );
                                break;
                            }
                        }
                    }
                }
            }

            foreach( AttributeType attribute in attributesToRemove )
            {
                attributes.RemoveElement( attribute );
            }

            foreach( AttributeType attribute in attributes )
            {
                string subPath = path + " " + attribute.Name;
                RemoveTypeOnlyAttributes( attribute.Attribute, subPath );
            }   
        }

        #endregion
    }
}