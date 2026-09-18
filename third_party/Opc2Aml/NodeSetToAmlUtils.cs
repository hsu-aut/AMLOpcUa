using System;
using System.Globalization;
using System.Collections.Generic;
using System.IO;
using System.Xml;
using System.Xml.Serialization;
using System.Reflection;
using System.Text;
using Opc.Ua;
using System.Net;
using Newtonsoft.Json.Linq;
using System.Linq;

namespace NodeSetToAmlUtils
{
    // AMLOpcUa patch 0002: OPC UA .NET Standard 1.5.378 seals ExpandedNodeId,
    // so this class wraps one instead of deriving from it. Opc2Aml only ever
    // constructs it from (NodeId, namespace URI, prefix) and formats it; the
    // output is unchanged: "prefix;nsu=<uri>;<id>", URL-encoded because
    // AutomationML does not allow '/' in IDs.
    public class AmlExpandedNodeId
    {
        private readonly ExpandedNodeId m_id;

        public AmlExpandedNodeId(NodeId nodeId, string namespaceUri, string prefix = null)
        {
            m_id = new ExpandedNodeId(nodeId, namespaceUri);
            Prefix = prefix;
        }

        /// <summary>The AML prefix string (can be null).</summary>
        public string Prefix { get; }

        public string Format()
        {
            StringBuilder buffer = new StringBuilder();
            if (!String.IsNullOrEmpty(Prefix))
            {
                buffer.Append(Prefix);
                buffer.Append(';');
            }
            ExpandedNodeId.Format(buffer, m_id.Identifier, m_id.IdType, 0, m_id.NamespaceUri, 0);
            return WebUtility.UrlEncode(buffer.ToString());
        }

        public override string ToString()
        {
            return Format();
        }
    }

}