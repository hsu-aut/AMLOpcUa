using MarkdownProcessor;
using MarkdownProcessor.NodeSet;
using System.Collections.Generic;
using System.Linq;

namespace Opc2Aml
{
    public class NamespaceInfo
    {
        public NamespaceInfo( string uri, HashSet<string> dependencies )
        {
            Uri = uri;
            Dependencies = dependencies.ToList<string>();
        }

        public int Count()
        {
            return Dependencies.Count;
        }

        public ushort Index { get; set; }
        public string Uri { get; set; }
        public List<string> Dependencies { get; set; }
    }

    internal class OrderModelInfo
    {
        public List<ModelInfo> GetProcessOrder( List<ModelInfo> initial )
        {
            List<ModelInfo> processed = new List<ModelInfo>();

            Dictionary<string, ModelInfo> initialSet = new Dictionary<string, ModelInfo>();

            foreach( ModelInfo modelInfo in initial )
            {
                initialSet.Add( modelInfo.NamespaceUri, modelInfo );
            }

            Dictionary<string, HashSet<string>> compiled = new Dictionary<string, HashSet<string>>();

            foreach( KeyValuePair<string, ModelInfo> pair in initialSet )
            {
                HashSet<string> dependencies = new HashSet<string>();
                AddCompiled( pair.Key, dependencies, initialSet );
                compiled.Add( pair.Key, dependencies );
            }

            List<NamespaceInfo> buildNamespaces = new List<NamespaceInfo>();

            // find the one with the most dependencies, the least dependencies
            foreach( KeyValuePair<string, HashSet<string>> pair in compiled )
            {
                buildNamespaces.Add( new NamespaceInfo( pair.Key, pair.Value ) );
            }

            List<NamespaceInfo> alphabetic = buildNamespaces.OrderBy( uri => uri.Uri ).ToList();
            List<NamespaceInfo> ordered = alphabetic.OrderBy( count => count.Count() ).ToList();

            foreach( NamespaceInfo buildNamespace in ordered )
            {
                processed.Add( initialSet[ buildNamespace.Uri ] );
            }

            return processed;
        }

        public void AddCompiled( string uri, HashSet<string> set,
            Dictionary<string, ModelInfo> source )
        {
            if( source.TryGetValue( uri, out ModelInfo modelInfo ) )
            {
                if ( modelInfo.NodeSet != null && 
                    modelInfo.NodeSet.Models != null )
                {
                    foreach( ModelTableEntry entry in modelInfo.NodeSet.Models )
                    {
                        if ( entry.RequiredModel != null )
                        {
                            foreach( ModelTableEntry requiredModel in entry.RequiredModel)
                            {
                                if ( !string.IsNullOrEmpty( requiredModel.ModelUri ) )
                                {
                                    set.Add( requiredModel.ModelUri );
                                    AddCompiled( requiredModel.ModelUri, set, source );
                                }
                            }
                        }
                    }   
                }
            }
        }
    }
}
