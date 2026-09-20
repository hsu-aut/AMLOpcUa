# Makes the planned MPS 500 addressable: every element of the plant structure
# that the served model also holds gets the NodeId of that node, in the form
# OPC 10000-83 Annex A gives it.
#
# The plugin then links the mirrored node to the planned element with refBaseObj
# ("0 linked to the plan" turns into a number), which is the point of the demo:
# the planned station and the running station are two aspects of one thing.
#
#   python plan.py <plant structure.aml> <out.aml>
#
# Defaults: MPS500_PlantStructure.aml of the MPS500 folder, out beside it.

import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

NODESET = Path(__file__).with_name('MPS500.NodeSet2.xml')
PLAN_IN = Path(r'C:\Dev\02_VDI3682\MPS500\AML\MPS500_PlantStructure.aml')
PLAN_OUT = Path(r'C:\Dev\Demo\opcua\MPS500_Plan.aml')
MODEL = 'http://hsu-hh.de/UA/MPS500/'
UA = '{http://opcfoundation.org/UA/2011/03/UANodeSet.xsd}'
CAEX = 'http://www.dke.de/CAEX'


def ua_paths():
    """The instances of the served model, as path -> numeric identifier."""
    root = ET.parse(NODESET).getroot()
    uris = [u.text for u in root.findall(f'{UA}NamespaceUris/{UA}Uri')]
    index = uris.index(MODEL) + 1
    nodes, children = {}, {}
    for node in root:
        if not node.tag.endswith('UAObject') and not node.tag.endswith('UAVariable'):
            continue
        node_id = node.get('NodeId')
        name = node.get('BrowseName', '').split(':')[-1]
        nodes[node_id] = name
        parent = node.get('ParentNodeId')
        if parent:
            children.setdefault(parent, []).append(node_id)
        else:
            for ref in node.findall(f'{UA}References/{UA}Reference'):
                if ref.get('ReferenceType') == 'Organizes' and ref.get('IsForward') == 'false':
                    children.setdefault(ref.text, []).append(node_id)

    paths = {}

    def walk(node_id, path):
        for child in children.get(node_id, []):
            child_path = path + '/' + nodes[child]
            paths[child_path] = child
            walk(child, child_path)

    line = next(i for i, n in nodes.items() if n == 'MPS500')
    paths['MPS500'] = line
    walk(line, 'MPS500')
    return {p: i for p, i in paths.items() if i.startswith(f'ns={index};i=')}


def add_node_id(element, identifier):
    """The NodeId attribute as Annex A writes it, with namespace and numeric id."""
    attribute = ET.SubElement(element, f'{{{CAEX}}}Attribute')
    attribute.set('Name', 'NodeId')
    attribute.set('RefAttributeType', '[ATL_http://opcfoundation.org/UA/]/[NodeId]')
    root_id = ET.SubElement(attribute, f'{{{CAEX}}}Attribute')
    root_id.set('Name', 'RootNodeId')
    root_id.set('RefAttributeType', 'ATL_OpcAmlMetaModel/ExplicitNodeId')
    namespace = ET.SubElement(root_id, f'{{{CAEX}}}Attribute')
    namespace.set('Name', 'NamespaceUri')
    namespace.set('AttributeDataType', 'xs:anyURI')
    namespace.set('RefAttributeType', 'ATL_OpcAmlMetaModel/NamespaceUri')
    ET.SubElement(namespace, f'{{{CAEX}}}Value').text = MODEL
    numeric = ET.SubElement(root_id, f'{{{CAEX}}}Attribute')
    numeric.set('Name', 'NumericId')
    numeric.set('AttributeDataType', 'xs:long')
    ET.SubElement(numeric, f'{{{CAEX}}}Value').text = identifier


def main():
    source = Path(sys.argv[1]) if len(sys.argv) > 1 else PLAN_IN
    target = Path(sys.argv[2]) if len(sys.argv) > 2 else PLAN_OUT
    paths = ua_paths()
    by_name = {}
    for path, node_id in paths.items():
        by_name.setdefault(path.split('/')[-1], []).append((path, node_id))

    ET.register_namespace('', CAEX)
    tree = ET.parse(source)
    written = 0

    def walk(element, path):
        nonlocal written
        for child in element.findall(f'{{{CAEX}}}InternalElement'):
            name = child.get('Name', '')
            child_path = path + '/' + name if path else name
            # The plant structure has two levels above the stations that the UA
            # model does not have; a station and everything below it is matched
            # by the tail of the path.
            for ua_path, node_id in by_name.get(name, []):
                if child_path.endswith(ua_path.split('MPS500/')[-1]):
                    add_node_id(child, node_id.split('i=')[-1])
                    written += 1
                    break
            walk(child, child_path)

    for hierarchy in tree.getroot().findall(f'{{{CAEX}}}InstanceHierarchy'):
        walk(hierarchy, '')

    target.parent.mkdir(parents=True, exist_ok=True)
    tree.write(target, encoding='utf-8', xml_declaration=True)
    print(f'{written} of {len(paths)} nodes written into {target}')


main()
