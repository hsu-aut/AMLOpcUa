using MarkdownProcessor;
using System.Collections.Generic;

namespace Opc2Aml
{
    public class NonHierarchicalReferences
    {
        List<NonHierarchicalReferenceHolder> References = null;
        ModelManager m_modelManager = null;

        public HashSet<string> BlackList = new HashSet<string>();
        public HashSet<string> LimitedList = new HashSet<string>();

        public NonHierarchicalReferences(ModelManager modelManager)
        {
            References = new List<NonHierarchicalReferenceHolder>();
            m_modelManager = modelManager;

            // Known Blacklist Items
            BlackList.Add(Opc.Ua.ReferenceTypeIds.HasTypeDefinition.ToString());
            BlackList.Add(Opc.Ua.ReferenceTypeIds.HasModellingRule.ToString());

            // All subsequent LimitedList items have the following errors and description
            // InternalLink has reference to an object out of AllowedScope
            // The referenced CAEX-Element via RefPartnerSideB is defined outside the class or
            // library bounds of this object, which is not allowed

            LimitedList.Add(Opc.Ua.ReferenceTypeIds.HasCondition.ToString());
            LimitedList.Add(Opc.Ua.ReferenceTypeIds.HasEffect.ToString());
            LimitedList.Add(Opc.Ua.ReferenceTypeIds.GeneratesEvent.ToString());
            LimitedList.Add(Opc.Ua.ReferenceTypeIds.AlwaysGeneratesEvent.ToString());
        }

        public List<NonHierarchicalReferenceHolder> ReferenceList { get { return References; } }

        public void AddReference(ReferenceInfo reference, bool instance)
        {
            string referenceTypeId = reference.ReferenceTypeId.ToString();

            if (!BlackList.Contains(referenceTypeId))
            {
                bool limited = false;

                if (LimitedList.Contains(referenceTypeId))
                {
                    limited = true;
                }

                NonHierarchicalReferenceHolder referenceHolder = new NonHierarchicalReferenceHolder(
                    reference, instance, limited);
                References.Add(referenceHolder);

            }
        }
    }

    public class NonHierarchicalReferenceHolder
    {
        public NonHierarchicalReferenceHolder(ReferenceInfo reference, bool instance, bool limited)
        {
            Reference = reference;
            Instance = instance;
            Limited = limited;
        }

        public ReferenceInfo Reference { get; set; }
        public bool Instance { get; set; }
        public bool Limited { get; set; }
    }
}
