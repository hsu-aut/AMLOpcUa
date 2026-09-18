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
    public class AmlExpandedNodeId : ExpandedNodeId
    {
        /// <summary>
        /// Extends a ExpandedNodeId by adding a Prefix string and formatting with URL encoding
        /// </summary>
        /// <remarks>
        /// Extends a ExpandedNodeId by adding a Prefix string and formatting with URL encoding
        /// </remarks>
        
        
        #region Constructors
        /// <summary>
        /// Initializes the object with default values.
        /// </summary>
        /// <remarks>
        /// Creates a new instance of the object, accepting the default values.
        /// </remarks> 
        // internal AmlExpandedNodeId() 
        // {
        //    Initialize(); 
        // }

        /// <summary>
        /// Creates a deep copy of the value.
        /// </summary>
        /// <remarks>
        /// Creates a new instance of the object, while copying the properties of the specified object.
        /// </remarks>
        /// <param name="value">The AmlExpandedNodeId to copy</param>
        /// <exception cref="ArgumentNullException">Thrown when the parameter is null</exception>
        public AmlExpandedNodeId(AmlExpandedNodeId value) : base( value )
        {
            Prefix = value.Prefix;
           
        }

        /// <summary>
        /// Creates a deep copy of the value from the base class.
        /// </summary>
        /// <remarks>
        /// Creates a new instance of the object, while copying the properties of the specified object.
        /// </remarks>
        /// <param name="value">The ExpandedNodeId to copy</param>
        /// <exception cref="ArgumentNullException">Thrown when the parameter is null</exception>
        public AmlExpandedNodeId(ExpandedNodeId value) : base(value)
        {
            Prefix = null;

        }

        /// <summary>
        /// Initializes an expanded node identifier with a node id and a namespace URI.
        /// </summary>
        /// <remarks>
        /// Creates a new instance of the object while allowing you to specify both the
        /// <see cref="NodeId"/> and the Namespace URI that applies to the NodeID.
        /// </remarks>
        /// <param name="nodeId">The <see cref="NodeId"/> to wrap.</param>
        /// <param name="namespaceUri">The namespace that this node belongs to</param>
        /// <param name="prefix">The optional AML prefix string (ascii chars only excluding ';' and '=' )</param>
        public AmlExpandedNodeId(NodeId nodeId, string namespaceUri, string prefix = null) : base(nodeId, namespaceUri)
        {
            Prefix= prefix;
        }

        /// <summary>
        /// Initializes a numeric node identifier with a namespace URI.
        /// </summary>
        /// <remarks>
        /// Creates a new instance of the class while accepting both the numeric id of the
        /// node, along with the actual namespace that this node belongs to.
        /// </remarks>
        /// <param name="value">The numeric id of the node we are wrapping</param>
        /// <param name="namespaceUri">The namespace that this node belongs to</param>
        /// <param name="prefix">The optional AML prefix string (ascii chars only excluding ';' and '=' )</param>
        public AmlExpandedNodeId(uint value, string namespaceUri, string prefix = null ) : base(value, namespaceUri) 
        {
            Prefix = prefix;
        }


        /// <summary>
        /// Initializes a string node identifier with a namespace URI.
        /// </summary>
        /// <remarks>
        /// Creates a new instance of the class while allowing you to specify both the node and namespace
        /// </remarks>
        /// <param name="namespaceUri">The actual namespace URI that this node belongs to</param>
        /// <param name="value">The string value/id of the node we are wrapping</param>
        /// <param name="prefix">The optional AML prefix string (ascii chars only excluding ';' and '=' )</param>
        public AmlExpandedNodeId(string value, string namespaceUri, string prefix = null) : base(value, namespaceUri)
        {
            Prefix = prefix;
        }

        /// <summary>
        /// Initializes a guid node identifier.
        /// </summary>
        /// <remarks>
        /// Creates a new instance of the class while specifying the <see cref="Guid"/> value
        /// of the node and the namespaceUri we are wrapping.
        /// </remarks>
        /// <param name="value">The Guid value of the node we are wrapping</param>
        /// <param name="namespaceUri">The namespace that this node belongs to</param>
        /// <param name="prefix">The optional AML prefix string (ascii chars only excluding ';' and '=' )</param>
        public AmlExpandedNodeId(Guid value, string namespaceUri, string prefix = null) :base(value, namespaceUri)
        {
            Prefix = prefix;
        }

        /// <summary>
        /// Initializes an opaque node identifier with a namespace index.
        /// </summary>
        /// <remarks>
        /// Creates a new instance of the class while allowing you to specify the node and namespace.
        /// </remarks>
        /// <param name="value">The node we are wrapping</param>
        /// <param name="namespaceUri">The namespace that this node belongs to</param>
        /// <param name="prefix">The optional AML prefix string (ascii chars only excluding ';' and '=' )</param>
        public AmlExpandedNodeId(byte[] value, string namespaceUri, string prefix = null) :base (value, namespaceUri)
        {
            Prefix = prefix;
        }

        /// <summary>
        /// Sets the private members to default values.
        /// </summary>
        /// <remarks>
        /// Sets the private members to default values.
        /// </remarks>
        private void Initialize()
        {
            Prefix = null;
            
        }
        #endregion  // Constructors

        #region Public Properties

        /// <summary>
        /// The AML prefix string
        /// </summary>
        /// <remarks>
        /// Returns the prifix (can be null )
        /// </remarks>
        public string Prefix
        {
            get { return m_Prefix; }
            protected set 
            { 
                // TODO validate Ascii chars with no ; or =
                m_Prefix = value; 
            }
        }


        #endregion // Public Properties

        #region public string Format()
        /// <summary>
        /// Formats a AML expanded node id as a string.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Formats a AmlExpandedNodeId as a string.
        /// <br/></para>
        /// <para>
        /// An example of this would be:
        /// <br/></para>
        /// <para>
        /// Prefix = "SomeASCIIstring"<br/>
        /// NodeId = "hello123"<br/>
        /// NamespaceUri = "http://mycompany/"<br/>
        /// <br/> This would translate into:<br/>
        /// SomeASCIIstring;nsu=http://mycompany/;s=hello123
        /// <br/>
        /// </para>
        /// <para>
        /// Note: Only information already included in the AmlExpandedNodeId-Instance will be included in the result and the result will be URL-encoded
        /// </para>
        /// </remarks>
        public new string Format()
        {
            StringBuilder buffer = new StringBuilder();
            Format(buffer);
            return WebUtility.UrlEncode(buffer.ToString());  //url encode the string because AutomationML does not allow '/' in IDs
        }

        /// <summary>
        /// Formats the node ids as string and adds it to the buffer.
        /// </summary>
        public new void Format(StringBuilder buffer)
        {
           
           Format(buffer, Prefix, Identifier, IdType, NamespaceUri);
           
        }

        /// <summary>
        /// Formats the node ids as string and adds it to the buffer.
        /// </summary>
        public static void Format(
            StringBuilder buffer,
            string prefix,
            object identifier,
            IdType identifierType,
            string namespaceUri )
        {

            // add  prefix
            if (!String.IsNullOrEmpty(prefix))
            {
                buffer.Append(prefix);
                buffer.Append(';');
            }

            ExpandedNodeId.Format(buffer, identifier, identifierType, 0, namespaceUri, 0);

        }
        #endregion

        #region IFormattable Members
        /// <summary>
        /// Returns the string representation of an ExpandedNodeId.
        /// </summary>
        /// <remarks>
        /// Returns the string representation of an ExpandedNodeId.
        /// </remarks>
        /// <returns>The <see cref="ExpandedNodeId"/> as a formatted string</returns>
        /// <param name="format">(Unused) The format string.</param>
        /// <param name="formatProvider">(Unused) The format-provider.</param>
        /// <exception cref="FormatException">Thrown when the 'format' parameter is NOT null. So leave that parameter null.</exception>
        public new string ToString(string format, IFormatProvider formatProvider)
        {
            if (format == null)
            {
                return Format();
            }

            throw new FormatException(Utils.Format("Invalid format string: '{0}'.", format));
        }

        /// <summary>
        /// Returns the string representation of am ExpandedNodeId.
        /// </summary>
        /// <remarks>
        /// Returns the string representation of am ExpandedNodeId.
        /// </remarks>
        public override string ToString()
        {
            return ToString(null, null);
        }
        #endregion

        #region IComparable Members
        /// <summary>
        /// Compares the current instance to the object.
        /// </summary>
        /// <remarks>
        /// Compares the current instance to the object.
        /// </remarks>
        public new int CompareTo(object obj)
        {
            // check for null.
            if (Object.ReferenceEquals(obj, null))
            {
                return -1;
            }

            // check for reference comparisons.
            if (Object.ReferenceEquals(this, obj))
            {
                return 0;
            }

            AmlExpandedNodeId aId = obj as AmlExpandedNodeId;

            ExpandedNodeId ThisBase = this as ExpandedNodeId;

            if( aId!= null ) 
            {
                if (Prefix == aId.Prefix)
                    return ThisBase.CompareTo(obj);
                else
                    return Prefix.CompareTo(aId.Prefix);    
            }
            if( Prefix == null)
            {
                return ThisBase.CompareTo(obj);
            }

            return -1;

        }

        /// <summary>
        /// Returns true if a is greater than b.
        /// </summary>
        /// <remarks>
        /// Returns true if a is greater than b.
        /// </remarks>
        public static bool operator >(AmlExpandedNodeId value1, object value2)
        {
            if (!Object.ReferenceEquals(value1, null))
            {
                return value1.CompareTo(value2) > 0;
            }

            return false;
        }

        /// <summary>
        /// Returns true if a is less than b.
        /// </summary>
        /// <remarks>
        /// Returns true if a is less than b.
        /// </remarks>
        public static bool operator <(AmlExpandedNodeId value1, object value2)
        {
            if (!Object.ReferenceEquals(value1, null))
            {
                return value1.CompareTo(value2) < 0;
            }

            return true;
        }
        #endregion

        #region Comparison Functions
        /// <summary>
        /// Determines if the specified object is equal to the ExpandedNodeId.
        /// </summary>
        /// <remarks>
        /// Determines if the specified object is equal to the ExpandedNodeId.
        /// </remarks>
        public override bool Equals(object obj)
        {
            return (CompareTo(obj) == 0);
        }

        /// <summary>
        /// Returns a unique hashcode for the ExpandedNodeId
        /// </summary>
        /// <remarks>
        /// Returns a unique hashcode for the ExpandedNodeId
        /// </remarks>
        public override int GetHashCode()
        {
            
            return (this as ExpandedNodeId).GetHashCode();
        }

        /// <summary>
        /// Returns true if the objects are equal.
        /// </summary>
        /// <remarks>
        /// Returns true if the objects are equal.
        /// </remarks>
        public static bool operator ==(AmlExpandedNodeId value1, object value2)
        {
            if (Object.ReferenceEquals(value1, null))
            {
                return Object.ReferenceEquals(value2, null);
            }

            return (value1.CompareTo(value2) == 0);
        }

        /// <summary>
        /// Returns true if the objects are not equal.
        /// </summary>
        /// <remarks>
        /// Returns true if the objects are not equal.
        /// </remarks>
        public static bool operator !=(AmlExpandedNodeId value1, object value2)
        {
            if (Object.ReferenceEquals(value1, null))
            {
                return !Object.ReferenceEquals(value2, null);
            }

            return (value1.CompareTo(value2) != 0);
        }
        #endregion

        #region public static AmlExpandedNodeId Parse(string text)
        /// <summary>
        /// Parses a expanded node id string and returns a node id object.
        /// </summary>
        /// <remarks>
        /// Parses a ExpandedNodeId String and returns a NodeId object
        /// </remarks>
        /// <param name="text">The ExpandedNodeId value as a string.</param>
        /// <exception cref="ServiceResultException">Thrown under a variety of circumstances, each time with a specific message.</exception>
        public new static AmlExpandedNodeId Parse(string text)
        {
            string prefix = null;
            string test = WebUtility.UrlDecode(text);
            if (!String.IsNullOrEmpty(test))
            {

                int iDelim = test.IndexOf(';');
                if (iDelim >= 0) // delimiter found
                {
                    int iEq = test.IndexOf('=', 0, iDelim);
                    if (iEq < 0)  // no equal sign in the first token so it must be the prefix
                    {
                        string[] split = test.Split(';', 2);
                        
                        prefix = split[0];
                        test = split[1];  // remove the prefix from test
                    }

                    ExpandedNodeId eni = ExpandedNodeId.Parse(test);  // process the remainder of the text
                    AmlExpandedNodeId rtn = new AmlExpandedNodeId(eni);
                    if ( prefix != null) 
                    {
                        rtn.Prefix = prefix;
                    }
                    return rtn;
                }
                else  // the entire string may be just a guid (e.g. generated by the AutomationML Editor)
                {
                    Guid g = new Guid(test);
                    if( !g.Equals( Guid.Empty ) ) 
                    {
                        return new AmlExpandedNodeId(g, null);
                    }
                }
             }
            return null;
        }
        #endregion  //public static AmlExpandedNodeId Parse(string text)

        #region Private Fields

        private string m_Prefix;
        
        #endregion
    }

}