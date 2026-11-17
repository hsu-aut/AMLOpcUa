// Checks a UAFX Descriptor against OPC 10000-83 clause 7 and the UAFX Offline
// Security Policies of OPC 10000-84: the container, the manifest, the Root AML
// files, the relationships, the content types, and every signature with its
// certificates (Table 3). Embedded Descriptors are checked the same way; what
// is found in one is named with its path ("/descriptors/vendor.amlx!/...").
//
// Revocation lists are not looked up (Table 3, Find Revocation List): the
// check runs offline. Trust comes from a list of trusted certificates and a
// list of issuer certificates (7.8.2).

using System.IO.Compression;
using System.IO.Packaging;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Xml;
using System.Xml.Linq;
using OpcUaAml.Checks;

namespace OpcUaAml.Descriptors;

/// <summary>The certificates a check trusts, and those it may use to build chains (OPC 10000-83 7.8.2).</summary>
public sealed class DescriptorTrust
{
    public static readonly DescriptorTrust None = new();

    public DescriptorTrust(IEnumerable<X509Certificate2>? trusted = null, IEnumerable<X509Certificate2>? issuers = null)
    {
        Trusted = (trusted ?? Enumerable.Empty<X509Certificate2>()).ToList();
        Issuers = (issuers ?? Enumerable.Empty<X509Certificate2>()).ToList();
    }

    public IReadOnlyList<X509Certificate2> Trusted { get; }

    public IReadOnlyList<X509Certificate2> Issuers { get; }

    /// <summary>Certificates from files (DER or PEM: .der, .cer, .crt, .pem) and from the files of folders.</summary>
    public static IReadOnlyList<X509Certificate2> Load(IEnumerable<string> paths)
    {
        var certificates = new List<X509Certificate2>();
        foreach (var path in paths)
        {
            var files = Directory.Exists(path)
                ? Directory.EnumerateFiles(path).Where(f => Path.GetExtension(f).ToLowerInvariant() is ".der" or ".cer" or ".crt" or ".pem")
                : new[] { path };
            foreach (var file in files)
            {
                var text = Path.GetExtension(file).Equals(".pem", StringComparison.OrdinalIgnoreCase) ? File.ReadAllText(file) : null;
                if (text != null)
                {
                    var all = new X509Certificate2Collection();
                    all.ImportFromPem(text);
                    certificates.AddRange(all);
                }
                else certificates.Add(new X509Certificate2(File.ReadAllBytes(file)));
            }
        }
        return certificates;
    }
}

/// <summary>A signature of a Descriptor with what its certificates say.</summary>
public sealed class SignatureVerdict
{
    public SignatureVerdict(PackageSignature signature) => Signature = signature;

    public PackageSignature Signature { get; }

    /// <summary>Whether the chain of the signer could be built up to a root.</summary>
    public bool ChainComplete { get; init; }

    /// <summary>Whether the signer or a certificate of its chain is trusted.</summary>
    public bool Trusted { get; init; }

    /// <summary>The chain, signer first.</summary>
    public IReadOnlyList<X509Certificate2> Chain { get; init; } = Array.Empty<X509Certificate2>();

    /// <summary>A short name of the signer: the common name of its certificate.</summary>
    public string SignerName => DescriptorChecker.NameOf(Signature.Signer) ?? Signature.Part.OriginalString;
}

/// <summary>What the check of a Descriptor found.</summary>
public sealed class DescriptorReport
{
    public DescriptorReport(string name) => Name = name;

    /// <summary>The file name, or the part of an embedded Descriptor.</summary>
    public string Name { get; }

    public DescriptorInfo? Info { get; init; }

    public string? ManifestPart { get; init; }

    public IReadOnlyList<string> RootFiles { get; init; } = Array.Empty<string>();

    /// <summary>Files the Root AML files relate to as AnyContent.</summary>
    public IReadOnlyList<string> Attachments { get; init; } = Array.Empty<string>();

    /// <summary>AML files the Root AML files relate to as Library or UafxInformationModel.</summary>
    public IReadOnlyList<string> Libraries { get; init; } = Array.Empty<string>();

    public IReadOnlyList<SignatureVerdict> Signatures { get; init; } = Array.Empty<SignatureVerdict>();

    public IReadOnlyList<DescriptorReport> Embedded { get; init; } = Array.Empty<DescriptorReport>();

    /// <summary>Every finding, those of embedded Descriptors included.</summary>
    public IReadOnlyList<Finding> Findings { get; init; } = Array.Empty<Finding>();

    public bool HasErrors => Findings.Any(f => f.Severity == Severity.Error);
}

public static class DescriptorChecker
{
    private const string Whole = "/";
    private const string ContentTypesName = "[Content_Types].xml";
    private const string CertificateContentType = "application/vnd.openxmlformats-package.digital-signature-certificate";

    /// <summary>Checks the Descriptor in <paramref name="path"/>; <paramref name="now"/> is the time the certificates are measured at.</summary>
    public static DescriptorReport Check(string path, DescriptorTrust? trust = null, DateTimeOffset? now = null)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new DescriptorReport(Path.GetFileName(path))
            {
                Findings = new[] { Finding.Of(DescriptorRules.NotAPackage, Severity.Error, Whole, null, DescriptorTexts.NotAPackage, ex.Message) },
            };
        }
        return Check(bytes, Path.GetFileName(path), "", trust ?? DescriptorTrust.None, now ?? DateTimeOffset.Now);
    }

    /// <summary>Checks a Descriptor given as its bytes.</summary>
    public static DescriptorReport Check(byte[] descriptor, string name, DescriptorTrust? trust = null, DateTimeOffset? now = null) =>
        Check(descriptor, name, "", trust ?? DescriptorTrust.None, now ?? DateTimeOffset.Now);

    internal static string? NameOf(X509Certificate2? certificate)
    {
        if (certificate == null) return null;
        var name = certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
        return string.IsNullOrEmpty(name) ? certificate.Subject : name;
    }

    private static DescriptorReport Check(byte[] bytes, string name, string prefix, DescriptorTrust trust, DateTimeOffset now)
    {
        var findings = new List<Finding>();
        void Add(string rule, Severity severity, string path, string format, params object?[] args) =>
            findings.Add(Finding.Of(rule, severity, prefix + path, null, format,
                args.Select(a => Convert.ToString(a, System.Globalization.CultureInfo.InvariantCulture) ?? "").ToArray()));
        void AddProblem(string rule, Severity severity, string path, Problem problem) =>
            findings.Add(Finding.Of(rule, severity, prefix + path, null, problem.Format, problem.Args.ToArray()));

        Package package;
        try
        {
            package = Package.Open(new MemoryStream(bytes, writable: false), FileMode.Open, FileAccess.Read);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or XmlException or ArgumentException
                                       or NotSupportedException or InvalidOperationException or FormatException)
        {
            Add(DescriptorRules.NotAPackage, Severity.Error, Whole, DescriptorTexts.NotAPackage, ex.Message);
            return new DescriptorReport(name) { Findings = findings };
        }

        using (package)
        {
            var parts = package.GetParts().ToList();
            var byName = parts.ToDictionary(p => Key(p.Uri), StringComparer.Ordinal);
            var packageRelations = package.GetRelationships().Where(r => r.TargetMode == TargetMode.Internal).ToList();
            Uri Target(PackageRelationship r) => PackUriHelper.ResolvePartUri(r.SourceUri, r.TargetUri);

            // The common services: relationships, the origin, signatures and their certificates.
            var origin = PackageSignatures.OriginPart(package);
            var common = new HashSet<string>(StringComparer.Ordinal);
            foreach (var part in parts)
                if (PackUriHelper.IsRelationshipPartUri(part.Uri)
                    || part.ContentType.Equals(PackageSignatures.SignatureContentType, StringComparison.OrdinalIgnoreCase)
                    || part.ContentType.Equals(PackageSignatures.OriginContentType, StringComparison.OrdinalIgnoreCase)
                    || part.ContentType.Equals(CertificateContentType, StringComparison.OrdinalIgnoreCase))
                    common.Add(Key(part.Uri));
            if (origin != null) common.Add(Key(origin.Uri));

            // The manifest, by its content.
            var manifests = parts.Where(p => !common.Contains(Key(p.Uri)) && FxModels.IsManifestPart(p)).ToList();
            DescriptorInfo? info = null;
            string? manifestPart = null;
            if (manifests.Count == 0)
                Add(DescriptorRules.Manifest, Severity.Error, Whole, DescriptorTexts.NoManifest);
            else if (manifests.Count > 1)
                Add(DescriptorRules.Manifest, Severity.Error, Whole, DescriptorTexts.ManyManifests,
                    manifests.Count, string.Join(", ", manifests.Select(m => m.Uri.OriginalString)));
            else
            {
                var manifest = manifests[0];
                manifestPart = manifest.Uri.OriginalString;
                XDocument xml;
                using (var stream = manifest.GetStream(FileMode.Open, FileAccess.Read)) xml = SafeXml.Load(stream);
                info = DescriptorInfo.Read(xml, out var problems);
                foreach (var problem in problems) AddProblem(DescriptorRules.Manifest, Severity.Error, manifestPart, problem);
                var naming = packageRelations.Where(r => Key(Target(r)) == Key(manifest.Uri)).ToList();
                if (naming.Count == 0)
                    Add(DescriptorRules.Manifest, Severity.Warning, manifestPart, DescriptorTexts.ManifestNotNamed);
                else if (!naming.Any(r => r.RelationshipType == DescriptorRelationships.Manifest))
                    Add(DescriptorRules.Manifest, Severity.Warning, manifestPart, DescriptorTexts.ManifestOtherType,
                        naming[0].RelationshipType, DescriptorRelationships.Manifest);
            }

            // Every relationship points to a part the package holds.
            var edges = new Dictionary<string, List<(string Target, string Type)>>(StringComparer.Ordinal);
            void Relations(string source, IEnumerable<PackageRelationship> relations)
            {
                foreach (var r in relations)
                {
                    if (r.TargetMode != TargetMode.Internal) continue;
                    var target = Target(r);
                    if (!byName.ContainsKey(Key(target)))
                    {
                        Add(DescriptorRules.BrokenRelationship, Severity.Error, source, DescriptorTexts.BrokenRelationship,
                            r.Id, r.RelationshipType, target.OriginalString);
                        continue;
                    }
                    if (source == Whole) continue;
                    if (!edges.TryGetValue(Key(new Uri(source, UriKind.Relative)), out var list))
                        edges[Key(new Uri(source, UriKind.Relative))] = list = new List<(string, string)>();
                    list.Add((Key(target), r.RelationshipType));
                }
            }
            Relations(Whole, packageRelations);
            foreach (var part in parts.Where(p => !PackUriHelper.IsRelationshipPartUri(p.Uri)))
                Relations(part.Uri.OriginalString, part.GetRelationships());

            // The Root AML files.
            var roots = packageRelations.Where(r => r.RelationshipType == DescriptorRelationships.RootDocument)
                .Select(r => Key(Target(r))).Where(byName.ContainsKey).Distinct().ToList();
            if (!packageRelations.Any(r => r.RelationshipType == DescriptorRelationships.RootDocument))
                Add(DescriptorRules.Root, Severity.Error, Whole, DescriptorTexts.NoRoot);
            foreach (var root in roots)
            {
                var part = byName[root];
                (string LocalName, string Namespace)? element;
                using (var stream = part.GetStream(FileMode.Open, FileAccess.Read)) element = FxModels.RootOf(stream);
                if (element is not { LocalName: "CAEXFile" })
                {
                    Add(DescriptorRules.Root, Severity.Error, part.Uri.OriginalString, DescriptorTexts.RootNotAml,
                        element == null ? "not XML" : $"its root element is {element.Value.LocalName}");
                    continue;
                }
                using var again = part.GetStream(FileMode.Open, FileAccess.Read);
                if (!FxModels.UsesFx(again))
                    Add(DescriptorRules.Root, Severity.Warning, part.Uri.OriginalString, DescriptorTexts.RootNotFx);
            }

            // Reachable from the Root AML files; circles.
            var reached = new HashSet<string>(roots, StringComparer.Ordinal);
            var attachments = new List<string>();
            var libraries = new List<string>();
            var embeddedParts = new List<string>();
            var queue = new Queue<string>(roots);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (!edges.TryGetValue(current, out var targets)) continue;
                foreach (var (target, type) in targets)
                {
                    if (common.Contains(target)) continue;
                    var list = type switch
                    {
                        DescriptorRelationships.AnyContent => attachments,
                        DescriptorRelationships.Library or DescriptorRelationships.UafxInformationModel => libraries,
                        DescriptorRelationships.EmbeddedDescriptor => embeddedParts,
                        _ => null,
                    };
                    if (list != null && !list.Contains(target)) list.Add(target);
                    if (reached.Add(target)) queue.Enqueue(target);
                }
            }
            var exempt = new HashSet<string>(common, StringComparer.Ordinal);
            foreach (var m in manifests) exempt.Add(Key(m.Uri));
            foreach (var r in packageRelations.Where(r => r.RelationshipType is DescriptorRelationships.CoreProperties
                         or DescriptorRelationships.Thumbnail or DescriptorRelationships.Manifest or PackageSignatures.OriginRelationship))
                exempt.Add(Key(Target(r)));
            foreach (var part in parts)
            {
                var key = Key(part.Uri);
                if (!exempt.Contains(key) && !reached.Contains(key))
                    Add(DescriptorRules.Unreachable, Severity.Error, part.Uri.OriginalString, DescriptorTexts.Unreachable);
            }
            foreach (var circle in Circles(roots, edges, common))
                Add(DescriptorRules.Circular, Severity.Error, byName[circle[0]].Uri.OriginalString, DescriptorTexts.Circular,
                    string.Join(" → ", circle.Select(k => byName[k].Uri.OriginalString)));

            // [Content_Types].xml lists only the extensions in use.
            foreach (var extension in UnusedExtensions(bytes))
                Add(DescriptorRules.ContentTypes, Severity.Warning, "/" + ContentTypesName, DescriptorTexts.UnusedExtension, extension);

            // The signatures.
            var verdicts = new List<SignatureVerdict>();
            var signatures = PackageSignatures.Read(package);
            if (signatures.Count == 0) Add(DescriptorRules.NoSignature, Severity.Error, Whole, DescriptorTexts.NoSignature);
            var originRelationships = origin == null ? null : Key(PackUriHelper.GetRelationshipPartUri(origin.Uri));
            var signatureParts = parts.Where(p => p.ContentType.Equals(PackageSignatures.SignatureContentType, StringComparison.OrdinalIgnoreCase)).ToList();
            var outOfScope = new HashSet<string>(StringComparer.Ordinal);
            if (originRelationships != null) outOfScope.Add(originRelationships);
            foreach (var s in signatureParts)
            {
                outOfScope.Add(Key(s.Uri));
                outOfScope.Add(Key(PackUriHelper.GetRelationshipPartUri(s.Uri)));
            }
            foreach (var part in parts.Where(p => p.ContentType.Equals(CertificateContentType, StringComparison.OrdinalIgnoreCase)))
                outOfScope.Add(Key(part.Uri));

            foreach (var signature in signatures)
            {
                var signer = NameOf(signature.Signer) ?? signature.Part.OriginalString;
                var at = signature.Part.OriginalString;
                foreach (var problem in signature.Problems)
                {
                    var rule = problem.Format switch
                    {
                        DescriptorTexts.NoCertificate => DescriptorRules.NoCertificate,
                        DescriptorTexts.NoTime => DescriptorRules.NoTime,
                        _ => DescriptorRules.SignatureInvalid,
                    };
                    AddProblem(rule, Severity.Error, at, problem);
                }
                foreach (var changed in signature.ChangedParts)
                    Add(DescriptorRules.SignatureInvalid, Severity.Error, changed.OriginalString, DescriptorTexts.PartChanged, signer);
                foreach (var missing in signature.MissingParts)
                    Add(DescriptorRules.SignatureInvalid, Severity.Error, missing.OriginalString, DescriptorTexts.PartMissing, signer);
                var signed = new HashSet<string>(signature.SignedParts.Select(Key), StringComparer.Ordinal);
                if (signature.SignedParts.Count > 0)
                    foreach (var part in parts)
                    {
                        var key = Key(part.Uri);
                        if (!outOfScope.Contains(key) && !signed.Contains(key))
                            Add(DescriptorRules.NotSigned, Severity.Error, part.Uri.OriginalString, DescriptorTexts.NotSigned, signer);
                    }
                verdicts.Add(Judge(signature, signer, at, trust, now, Add));
            }

            // Embedded Descriptors, checked as Descriptors of their own.
            var embedded = new List<DescriptorReport>();
            foreach (var key in embeddedParts)
            {
                var part = byName[key];
                var report = Check(PackageSignatures.ReadAll(part), part.Uri.OriginalString, prefix + part.Uri.OriginalString + "!", trust, now);
                embedded.Add(report);
                findings.AddRange(report.Findings);
            }

            return new DescriptorReport(name)
            {
                Info = info,
                ManifestPart = manifestPart,
                RootFiles = roots.Select(k => byName[k].Uri.OriginalString).ToList(),
                Attachments = attachments.Select(k => byName[k].Uri.OriginalString).ToList(),
                Libraries = libraries.Select(k => byName[k].Uri.OriginalString).ToList(),
                Signatures = verdicts,
                Embedded = embedded,
                Findings = findings,
            };
        }
    }

    /// <summary>The certificates of a signature against Table 3 and the UAFX Offline Security Policies.</summary>
    private static SignatureVerdict Judge(PackageSignature signature, string signer, string at, DescriptorTrust trust, DateTimeOffset now,
        Action<string, Severity, string, string, object?[]> add)
    {
        void Add(string rule, Severity severity, string format, params object?[] args) => add(rule, severity, at, format, args);

        // Algorithms and key lengths.
        if (!Policies.Signature.TryGetValue(signature.SignatureMethod, out var policy))
            Add(DescriptorRules.Policy, Severity.Error, DescriptorTexts.SignatureAlgorithm, signer, signature.SignatureMethod);
        foreach (var digest in signature.DigestMethods.Where(d => !Policies.Digests.Contains(d)))
            Add(DescriptorRules.Policy, Severity.Error, DescriptorTexts.DigestAlgorithm, signer, digest);
        var certificate = signature.Signer;
        if (certificate == null) return new SignatureVerdict(signature);
        if (policy != null && KeySize(certificate) is int bits && (bits < policy.MinBits || bits > policy.MaxBits))
            Add(DescriptorRules.Policy, Severity.Error, DescriptorTexts.KeyLength, signer, bits, policy.Name, policy.MinBits, policy.MaxBits);

        // The signing certificate's use.
        var basic = certificate.Extensions.OfType<X509BasicConstraintsExtension>().FirstOrDefault();
        if (basic is { CertificateAuthority: true })
            Add(DescriptorRules.Usage, Severity.Error, DescriptorTexts.IsCa, NameOf(certificate));
        var usage = certificate.Extensions.OfType<X509KeyUsageExtension>().FirstOrDefault();
        if (usage == null)
            Add(DescriptorRules.Usage, Severity.Warning, DescriptorTexts.NoKeyUsage, NameOf(certificate));
        else if (!usage.KeyUsages.HasFlag(X509KeyUsageFlags.DigitalSignature))
            Add(DescriptorRules.Usage, Severity.Error, DescriptorTexts.NotForSigning, NameOf(certificate));

        // The chain, up to a root; time is checked by itself below.
        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.IgnoreNotTimeValid | X509VerificationFlags.IgnoreNotTimeNested
                                              | X509VerificationFlags.IgnoreCtlNotTimeValid;
        chain.ChainPolicy.DisableCertificateDownloads = true;
        chain.ChainPolicy.VerificationTime = (signature.Time ?? now).UtcDateTime;
        foreach (var c in signature.Certificates.Concat(trust.Trusted).Concat(trust.Issuers))
            (IsSelfSigned(c) ? chain.ChainPolicy.CustomTrustStore : chain.ChainPolicy.ExtraStore).Add(c);
        var built = chain.Build(certificate);
        var elements = chain.ChainElements.Select(e => e.Certificate).ToList();
        var serious = chain.ChainStatus.Where(s => (s.Status & ~(X509ChainStatusFlags.NotTimeValid | X509ChainStatusFlags.NotTimeNested
                                                                 | X509ChainStatusFlags.RevocationStatusUnknown | X509ChainStatusFlags.OfflineRevocation)) != 0).ToList();
        var complete = built || serious.Count == 0;
        if (!complete)
        {
            var why = serious.Any(s => s.Status.HasFlag(X509ChainStatusFlags.PartialChain))
                ? $"the issuer {elements.Last().Issuer} is not in the signature, the trusted or the issuer certificates."
                : string.Join(" ", serious.Select(s => s.StatusInformation.Trim()).Distinct());
            Add(DescriptorRules.Trust, Severity.Error, DescriptorTexts.ChainIncomplete, signer, why);
        }

        foreach (var c in elements.Count > 0 ? elements : new List<X509Certificate2> { certificate })
        {
            if (!Policies.CertificateSignatures.Contains(c.SignatureAlgorithm.Value ?? ""))
                Add(DescriptorRules.Policy, Severity.Error, DescriptorTexts.CertificateAlgorithm, NameOf(c),
                    c.SignatureAlgorithm.FriendlyName ?? c.SignatureAlgorithm.Value);
            if (signature.Time is { } time && (time < c.NotBefore || time > c.NotAfter))
                Add(DescriptorRules.Validity, Severity.Error, DescriptorTexts.SignedOutsideValidity, signer, Iso(time), NameOf(c),
                    Iso(c.NotBefore), Iso(c.NotAfter));
            if (now < c.NotBefore)
                Add(DescriptorRules.Validity, Severity.Error, DescriptorTexts.NotYetValid, NameOf(c), Iso(c.NotBefore));
        }

        var trusted = complete && elements.Any(e => trust.Trusted.Any(t => t.Thumbprint == e.Thumbprint));
        if (complete && !trusted)
            Add(DescriptorRules.Trust, Severity.Warning, DescriptorTexts.Untrusted, signer);
        return new SignatureVerdict(signature) { ChainComplete = complete, Trusted = trusted, Chain = elements };
    }

    private static string Iso(DateTimeOffset time) => time.ToString("yyyy-MM-dd HH:mm 'UTC'zzz", System.Globalization.CultureInfo.InvariantCulture);

    private static string Iso(DateTime time) => Iso(new DateTimeOffset(time));

    private static bool IsSelfSigned(X509Certificate2 certificate)
    {
        if (certificate.SubjectName.RawData.AsSpan().SequenceEqual(certificate.IssuerName.RawData) is false) return false;
        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(certificate);
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.IgnoreNotTimeValid;
        chain.ChainPolicy.DisableCertificateDownloads = true;
        return chain.Build(certificate) || chain.ChainStatus.All(s => !s.Status.HasFlag(X509ChainStatusFlags.NotSignatureValid));
    }

    private static int? KeySize(X509Certificate2 certificate)
    {
        using var rsa = certificate.GetRSAPublicKey();
        if (rsa != null) return rsa.KeySize;
        using var ec = certificate.GetECDsaPublicKey();
        return ec?.KeySize;
    }

    /// <summary>Circles of relationships among the parts the Root AML files lead to, each once.</summary>
    private static IEnumerable<List<string>> Circles(IEnumerable<string> roots, Dictionary<string, List<(string Target, string Type)>> edges,
        HashSet<string> common)
    {
        var state = new Dictionary<string, int>(StringComparer.Ordinal); // 1 on the path, 2 done
        var path = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var circles = new List<List<string>>();
        void Visit(string node)
        {
            state[node] = 1;
            path.Add(node);
            if (edges.TryGetValue(node, out var targets))
                foreach (var (target, _) in targets)
                {
                    if (common.Contains(target)) continue;
                    if (state.TryGetValue(target, out var s) && s == 1)
                    {
                        var circle = path.Skip(path.IndexOf(target)).Append(target).ToList();
                        if (seen.Add(string.Join("|", circle.Skip(1).OrderBy(k => k, StringComparer.Ordinal)))) circles.Add(circle);
                    }
                    else if (!state.ContainsKey(target)) Visit(target);
                }
            path.RemoveAt(path.Count - 1);
            state[node] = 2;
        }
        foreach (var root in roots)
            if (!state.ContainsKey(root)) Visit(root);
        return circles;
    }

    /// <summary>Extensions [Content_Types].xml declares a default for that no file in the container has.</summary>
    private static IEnumerable<string> UnusedExtensions(byte[] bytes)
    {
        try
        {
            using var zip = new ZipArchive(new MemoryStream(bytes, writable: false), ZipArchiveMode.Read);
            var entry = zip.GetEntry(ContentTypesName);
            if (entry == null) return Array.Empty<string>();
            XDocument types;
            using (var stream = entry.Open()) types = SafeXml.Load(stream);
            var used = zip.Entries.Where(e => e.FullName != ContentTypesName && !e.FullName.EndsWith('/'))
                .Select(e => Path.GetExtension(e.FullName).TrimStart('.').ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
            return types.Root?.Elements().Where(e => e.Name.LocalName == "Default")
                       .Select(e => ((string?)e.Attribute("Extension") ?? "").ToLowerInvariant())
                       .Where(x => x.Length > 0 && !used.Contains(x)).Distinct().ToList()
                   ?? new List<string>();
        }
        catch (Exception ex) when (ex is InvalidDataException or XmlException or IOException)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>Part names compare without case (ISO/IEC 29500-2, 6.2.2.3).</summary>
    private static string Key(Uri uri) => uri.OriginalString.ToUpperInvariant();

    /// <summary>The UAFX Offline Security Policies of OPC 10000-84 (6.6.5).</summary>
    private static class Policies
    {
        public sealed record SignaturePolicy(string Name, int MinBits, int MaxBits);

        public static readonly IReadOnlyDictionary<string, SignaturePolicy> Signature = new Dictionary<string, SignaturePolicy>
        {
            [PackageSignatures.RsaSha256] = new("Rsa-Pkcs-Sha256", 2048, 4096),
            [PackageSignatures.RsaSha384] = new("Rsa-Pkcs-Sha384", 3072, 4096),
            [PackageSignatures.RsaSha512] = new("Rsa-Pkcs-Sha512", 4096, 8192),
            [PackageSignatures.RsaPssSha256] = new("Rsa-Pss-Sha256", 2048, 4096),
            [PackageSignatures.RsaPssSha384] = new("Rsa-Pss-Sha384", 3072, 4096),
            [PackageSignatures.RsaPssSha512] = new("Rsa-Pss-Sha512", 4096, 8192),
            [PackageSignatures.EcdsaSha256] = new("ECC-nistP256", 256, 256),
            [PackageSignatures.EcdsaSha384] = new("ECC-nistP384", 384, 384),
            [PackageSignatures.EcdsaSha512] = new("ECC-nistP521", 521, 521),
        };

        public static readonly HashSet<string> Digests = new(StringComparer.Ordinal)
        {
            PackageSignatures.Sha256, PackageSignatures.Sha384, PackageSignatures.Sha512,
        };

        /// <summary>The OIDs of the certificate signature algorithms the policies allow.</summary>
        public static readonly HashSet<string> CertificateSignatures = new(StringComparer.Ordinal)
        {
            "1.2.840.113549.1.1.11", // sha256WithRSAEncryption
            "1.2.840.113549.1.1.12", // sha384WithRSAEncryption
            "1.2.840.113549.1.1.13", // sha512WithRSAEncryption
            "1.2.840.113549.1.1.10", // RSASSA-PSS
            "1.2.840.10045.4.3.2",   // ecdsa-with-SHA256
            "1.2.840.10045.4.3.3",   // ecdsa-with-SHA384
            "1.2.840.10045.4.3.4",   // ecdsa-with-SHA512
        };
    }
}
