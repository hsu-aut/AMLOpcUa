# UAFX Descriptors

A UAFX Descriptor (OPC 10000-83 v1.00.03, clauses 5 to 8) carries the
information model of an OPC UA FX AutomationComponent from one engineering
tool to another: an AML container (`.amlx`, Open Packaging Conventions) with
a manifest, one or more Root AML files, attachments such as manuals, embedded
Descriptors, and at least one digital signature. The plugin writes the open
document as a Descriptor and checks Descriptors it receives; `uaaml
descriptor` does the same on the command line.

## What is written

```
/station.aml                              Root AML file       RootDocument, from /_rels/.rels
/manifest.xml                             UAFX manifest       Manifest, from /_rels/.rels
/attachments/manual.pdf                   attachment          AnyContent, from the root
/descriptors/vendor.amlx                  embedded Descriptor EmbeddedDescriptor, from the root
/package/services/digital-signature/origin.psdsor            origin of the signatures
/package/services/digital-signature/xml-signature/<id>.psdsxs a signature
```

Part 83 prescribes no folders (7.3.1); relationships say what a file is. The
document goes in as it is in the editor, libraries included, so the Root AML
file holds the OPC UA FX AML libraries it is built on. An attached file that
is itself a Descriptor (a package holding a UAFX manifest) is embedded as one;
every other attachment is AnyContent. Content types follow Table 2
(`application/automationml-aml+xml`, `application/automationml-amlx+zip`),
and `[Content_Types].xml` lists only extensions in use (7.6).

The manifest follows the schema of Annex J, in its target namespace
`http://opcfoundation.org/UA/FX/2021/08/DescriptorInfo.xsd`:

```xml
<DescriptorInfo xmlns="http://opcfoundation.org/UA/FX/2021/08/DescriptorInfo.xsd">
  <DescriptorIdentifier>urn:hsu:mps500:station1</DescriptorIdentifier>
  <DescriptorVersion><Major>1</Major><Minor>0</Minor><Build>0</Build><SubBuild>0</SubBuild></DescriptorVersion>
  <OpcUaFxVersion>1.00.03</OpcUaFxVersion>
</DescriptorInfo>
```

`OpcUaFxVersion` is "taken from the NodeSet file the Descriptor adheres to"
(Table 1). The plugin reads it from the `OpcUaLibInfo` (Annex K) of the
document's library for `http://opcfoundation.org/UA/FX/AC/`, else of another
FX model; a document without FX library has to be given one.

## The signature

Each signature is an XML signature by ISO/IEC 29500-2 clause 13, built as
Annex I shows: `SignedInfo` (Canonical XML 1.1, RSA SHA-256 or ECDSA) signs
one `Object`, whose `Manifest` holds a digest of every part
(`/part?ContentType=…`) and whose `SignatureProperties` hold the
`SignatureTime`. Relationship parts and earlier signatures are digested
canonicalized, every other part as its bytes. The scope is every part but
`[Content_Types].xml` and the relationships of the origin part, which list the
signatures (7.8.1): a second signature covers the first and leaves it valid.
The signing certificate goes into `KeyInfo/X509Data`, with the chain given.

The key decides the algorithm, within the UAFX Offline Security Policies of
OPC 10000-84: RSA 2048 to 4096 bits signs with RSA PKCS#1 v1.5 and SHA-256
(Rsa-Pkcs-Sha256), a larger RSA key with SHA-512, ECC P-256, P-384 and P-521
with ECDSA and SHA-256, SHA-384 and SHA-512. Checking also reads RSA-PSS
(`http://www.w3.org/2007/05/xmldsig-more#sha256-rsa-MGF1` and its SHA-384 and
SHA-512 forms).

Canonical XML 1.1, which Annex I names, differs from 1.0 only where `xml:id`
and `xml:base` are inherited into a part of a document. A whole part has no
ancestors and these signatures carry no `xml:` attributes, so both give the
same octets; .NET implements 1.0, which serves for both names.

The signatures were tested against WPF's `PackageDigitalSignatureManager`, an
implementation of ISO/IEC 29500-2 written without this code: a package it
signs (with the Relationships Transform) verifies here, and a signature made
here with Canonical XML 1.0 verifies there.

## The check

| Rule | Severity | Finding |
|---|---|---|
| DX001 | Error | The file is not an AML container (an OPC package). |
| DX002 | Error | No UAFX manifest, more than one, or one that departs from Annex J. Warning: the manifest is found by its content, but no relationship or another type names it. |
| DX003 | Error | No Root AML file, or one that is not an AML document. Warning: it uses no OPC UA FX AML library (7.4). |
| DX004 | Error | A relationship points to a file the Descriptor does not hold. |
| DX005 | Error | A file cannot be reached from a Root AML file through relationships (7.5.2). |
| DX006 | Error | Relationships lead in a circle (7.5.2). |
| DX007 | Warning | `[Content_Types].xml` lists an extension no file has (7.6). |
| DX008 | Error | The Descriptor is not signed (7.8.1). |
| DX009 | Error | A signature does not verify, a signed file changed or is gone, or it covers no file. |
| DX010 | Error | A file is outside the scope of a signature (7.8.1), or a relationship that a Relationships Transform leaves out (the origin relationship aside). |
| DX011 | Error | A signature carries no X.509 certificate. |
| DX012 | Error | Signature, digest or certificate algorithm, key length or curve outside the UAFX Offline Security Policies (OPC 10000-84), for every certificate of the chain. |
| DX013 | Error | The signing certificate is not for digital signatures, or is a CA certificate (Table 3). Warning: it states no key usage. |
| DX014 | Error | A signature was made outside the validity of a certificate of its chain, or a certificate is not yet valid (Table 3). Warning: a certificate has expired since; that it was valid when signing rests on the time the signature gives. |
| DX015 | Error | The chain of the signer cannot be built, a certificate is revoked, or neither the signer nor a certificate of its chain is trusted (7.8.2). Warning: whether a certificate is revoked could not be checked. |
| DX016 | Error | A signature does not say when it was made (SignatureTime). |
| DX017 | Error | The container holds a zip entry that is no part of the package (no valid part name, another spelling of a part, a second entry of one): a program that unpacks the container takes it, and no signature covers it. |

Embedded Descriptors are checked the same way, four levels deep; what is
found in one is named with its path, as in
`/descriptors/vendor.amlx!/attachments/manual.pdf`. Which parts lie outside a
signature's scope follows from the relationships of the origin and the
signatures, not from content types: those come from `[Content_Types].xml`,
which no signature covers. A part that unpacks to more than 512 MB is not
read.

A signature is **valid** when its value verifies, no file it signs changed or
went, it covers every file and relationship, the container holds nothing
besides the parts, and no rule finds its algorithms, keys or certificates
wrong; it is **trusted** when it is valid and its signer is trusted. Trust is
never offered for a signature that is not valid.

Trust follows 7.8.2: a list of trusted certificates (the signer itself or a
CA that issued it, matched byte for byte) and a list of issuer certificates
for building chains. A signer nobody trusts is an error, so `uaaml descriptor
check` fails until it is trusted; the plugin offers **Trust signer…** with its
SHA-256 fingerprint. Revocation lists are looked up where a certificate below
the root names one (Table 3, Find Revocation List); one that cannot be read
is a warning rather than an error, as Descriptors are also checked offline,
which departs from Table 3. The plugin keeps its lists below
`%LOCALAPPDATA%\AMLOpcUa\descriptors`: `trusted` holds the certificates
trusted with **Trust signer…**, `signers` the own signing keys, whose
Descriptors count as trusted, each encrypted with DPAPI for the user's
account. Whoever runs as the user can add to `trusted`, as to the user's
certificate store of Windows. On the command line `--trust` and `--issuers`
name the certificates, and `uaaml descriptor certificate` writes a key file
only with a password.

The check does not look into the information model itself; `uaaml check`
does that for the Root AML file (rules UA001 to UA008).

## Where Part 83 leaves something open

Read in OPC 10000-83 v1.00.03 (2026-06-08):

- **The manifest's relationship type is empty.** 7.3.2 says the manifest
  "shall be identified by an Open Packaging Conventions relationship of
  type" and gives `""`, and Example 1 writes `Type=""`; the online reference
  and the PDF agree. For embedded Descriptors (7.3.5) the PDF keeps the type as a link
  target: `http://schemas.opcfoundation.org/container/relationship/EmbeddedDescriptor`.
  Figure 17 names every relationship by the last segment of its type
  (RootDocument, AnyContent, Library, EmbeddedDescriptor) and the manifest's
  "Manifest". The plugin therefore writes
  `http://schemas.opcfoundation.org/container/relationship/Manifest`, finds a
  manifest by its content (a part whose root element is `DescriptorInfo`),
  and only warns about another type.
- **Folder and file names of the signature differ.** 7.3.6 names
  `/package/service/digital-signature` and `origin.psdor`; 7.8.1, Figure 17
  and Annex I name `/package/services/digital-signature` and `origin.psdsor`,
  as ISO/IEC 29500-2 and other implementations do. The plugin writes the
  latter and finds the origin by its package relationship, wherever it is.
- **`DescriptorVersion`** is "4 integers of type xs:short" in Table 1 and
  `xs:unsignedShort` in the normative schema of Annex J; the plugin follows
  Annex J (0 to 65535).
- **The manifest's namespace.** Example 2 writes `DescriptorInfo` without
  namespace, while Annex J declares one with `elementFormDefault="qualified"`.
  The plugin writes the namespace and reads both, with a finding for the
  missing one.
