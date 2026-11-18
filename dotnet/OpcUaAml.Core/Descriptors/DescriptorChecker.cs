// Checks a UAFX Descriptor against OPC 10000-83 clause 7 and the UAFX Offline
// Security Policies of OPC 10000-84: the container, the manifest, the Root AML
// files, the relationships, the content types, and every signature with its
// certificates (Table 3). Embedded Descriptors are checked the same way; what
// is found in one is named with its path ("/descriptors/vendor.amlx!/...").
//
// Revocation lists are looked up where a certificate names one (Table 3,
// Find Revocation List); one that cannot be read is a warning, not an error,
// as Descriptors are checked offline too. Trust comes from a list of trusted
// certificates and a list of issuer certificates (7.8.2): a signer no one
// trusts is an error (7.8.2, 8.2), which the plugin lets the user resolve.

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

    /// <summary>Whether the signer or a certificate of its chain is in the list of trusted certificates.</summary>
    public bool SignerTrusted { get; init; }

    /// <summary>
    /// Whether the signature holds: its value verifies, nothing it signs changed
    /// or went, it covers every part and relationship, the container holds
    /// nothing besides the parts, and no rule found its algorithms, keys or
    /// certificates wrong. Whom it comes from is <see cref="SignerTrusted"/>.
    /// </summary>
    public bool Valid { get; init; }

    /// <summary>Valid, and its signer trusted: only then does the Descriptor come from whom it says.</summary>
    public bool Trusted => Valid && SignerTrusted;

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
    /// <summary>How deep Descriptors embedded in Descriptors are checked.</summary>
    private const int MaxDepth = 4;

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
        return Check(bytes, Path.GetFileName(path), trust, now);
    }

    /// <summary>Checks a Descriptor given as its bytes.</summary>
    public static DescriptorReport Check(byte[] descriptor, string name, DescriptorTrust? trust = null, DateTimeOffset? now = null)
    {
        try
        {
            return Check(descriptor, name, "", trust ?? DescriptorTrust.None, now ?? DateTimeOffset.Now, depth: 0);
        }
        // What a crafted file makes the libraries throw ends the check with a finding, not with the exception.
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            return new DescriptorReport(name)
            {
                Findings = new[] { Finding.Of(DescriptorRules.NotAPackage, Severity.Error, Whole, null, DescriptorTexts.NotAPackage, ex.Message) },
            };
        }
    }

    internal static string? NameOf(X509Certificate2? certificate)
    {
        if (certificate == null) return null;
        var name = certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
        return string.IsNullOrEmpty(name) ? certificate.Subject : name;
    }

    private static DescriptorReport Check(byte[] bytes, string name, string prefix, DescriptorTrust trust, DateTimeOffset now, int depth)
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

            // Entries the package library passes over as no part: a program that unpacks
            // the container takes them all the same, and no signature covers them.
            var stray = StrayEntries(bytes, parts);
            foreach (var entry in stray)
                Add(DescriptorRules.StrayEntry, Severity.Error, Whole, DescriptorTexts.StrayEntry, entry);

            // The common services: relationship parts, the origin, the signatures it names
            // and their certificates. By the relationships that name them, not by content
            // type: that comes from [Content_Types].xml, which no signature covers.
            var origin = PackageSignatures.OriginPart(package);
            var signatureParts = new List<PackagePart>();
            var certificateParts = new List<PackagePart>();
            if (origin != null)
                foreach (var r in origin.GetRelationshipsByType(PackageSignatures.SignatureRelationship).Where(r => r.TargetMode == TargetMode.Internal))
                {
                    if (!byName.TryGetValue(Key(Target(r)), out var signaturePart)) continue;
                    signatureParts.Add(signaturePart);
                    foreach (var c in signaturePart.GetRelationshipsByType(PackageSignatures.CertificateRelationship).Where(c => c.TargetMode == TargetMode.Internal))
                        if (byName.TryGetValue(Key(Target(c)), out var certificatePart)) certificateParts.Add(certificatePart);
                }
            var common = new HashSet<string>(StringComparer.Ordinal);
            foreach (var part in parts.Where(p => PackUriHelper.IsRelationshipPartUri(p.Uri))) common.Add(Key(part.Uri));
            if (origin != null) common.Add(Key(origin.Uri));
            foreach (var part in signatureParts.Concat(certificateParts)) common.Add(Key(part.Uri));

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
            // Outside every signature's scope (OPC 10000-83 7.8.1): the origin's relationships,
            // the signatures the origin names with their relationships and certificates.
            var originRelationships = origin == null ? null : Key(PackUriHelper.GetRelationshipPartUri(origin.Uri));
            var outOfScope = new HashSet<string>(StringComparer.Ordinal);
            if (originRelationships != null) outOfScope.Add(originRelationships);
            foreach (var s in signatureParts)
            {
                outOfScope.Add(Key(s.Uri));
                outOfScope.Add(Key(PackUriHelper.GetRelationshipPartUri(s.Uri)));
            }
            foreach (var part in certificateParts) outOfScope.Add(Key(part.Uri));

            foreach (var signature in signatures)
            {
                var before = findings.Count;
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
                if (signature.SignedParts.Count == 0)
                    Add(DescriptorRules.SignatureInvalid, Severity.Error, at, DescriptorTexts.SignsNothing, signer);
                else
                    foreach (var part in parts)
                    {
                        var key = Key(part.Uri);
                        if (!outOfScope.Contains(key) && !signed.Contains(key))
                            Add(DescriptorRules.NotSigned, Severity.Error, part.Uri.OriginalString, DescriptorTexts.NotSigned, signer);
                    }
                foreach (var (relationships, id, type) in signature.UncoveredRelationships)
                    Add(DescriptorRules.NotSigned, Severity.Error, relationships.OriginalString, DescriptorTexts.RelationshipNotSigned, id, type, signer);
                var judged = Judge(signature, signer, at, trust, now, Add);
                // Whom it comes from aside (an untrusted signer, an incomplete chain), any error
                // found on this signature, or an entry beside the parts, means it does not hold.
                var broken = findings.Skip(before).Any(f => f.Severity == Severity.Error
                                                            && (f.Rule != DescriptorRules.Trust || f.Format == DescriptorTexts.Revoked));
                verdicts.Add(new SignatureVerdict(signature)
                {
                    ChainComplete = judged.ChainComplete,
                    SignerTrusted = judged.SignerTrusted,
                    Chain = judged.Chain,
                    Valid = signature.ValueValid && !broken && stray.Count == 0,
                });
            }

            // Embedded Descriptors, checked as Descriptors of their own.
            var embedded = new List<DescriptorReport>();
            foreach (var key in embeddedParts)
            {
                var part = byName[key];
                if (depth + 1 > MaxDepth)
                {
                    Add(DescriptorRules.NotAPackage, Severity.Error, part.Uri.OriginalString, DescriptorTexts.TooDeep, MaxDepth);
                    continue;
                }
                byte[] inner;
                try { inner = PackageSignatures.ReadAll(part); }
                catch (Exception ex) when (ex is InvalidDataException or IOException)
                {
                    Add(DescriptorRules.NotAPackage, Severity.Error, part.Uri.OriginalString, DescriptorTexts.NotAPackage, ex.Message);
                    continue;
                }
                var report = Check(inner, part.Uri.OriginalString, prefix + part.Uri.OriginalString + "!", trust, now, depth + 1);
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
        if (policy?.Curve is { } curve && CurveOf(certificate) is { } actual && actual != curve)
            Add(DescriptorRules.Policy, Severity.Error, DescriptorTexts.Curve, signer, actual, policy.Name, curve);

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
        // Revocation lists where a certificate names one; the root, trusted as it is, has none to name.
        chain.ChainPolicy.RevocationMode = X509RevocationMode.Online;
        chain.ChainPolicy.RevocationFlag = X509RevocationFlag.ExcludeRoot;
        chain.ChainPolicy.UrlRetrievalTimeout = TimeSpan.FromSeconds(10);
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
                                                                 | X509ChainStatusFlags.RevocationStatusUnknown | X509ChainStatusFlags.OfflineRevocation
                                                                 | X509ChainStatusFlags.Revoked)) != 0).ToList();
        var complete = built || serious.Count == 0;
        if (!complete)
        {
            var why = serious.Any(s => s.Status.HasFlag(X509ChainStatusFlags.PartialChain))
                ? $"the issuer {elements.Last().Issuer} is not in the signature, the trusted or the issuer certificates."
                : string.Join(" ", serious.Select(s => s.StatusInformation.Trim()).Distinct());
            Add(DescriptorRules.Trust, Severity.Error, DescriptorTexts.ChainIncomplete, signer, why);
        }

        if (complete)
        {
            var revoked = chain.ChainElements.Where(e => e.ChainElementStatus.Any(s => s.Status.HasFlag(X509ChainStatusFlags.Revoked))).ToList();
            foreach (var e in revoked)
                Add(DescriptorRules.Trust, Severity.Error, DescriptorTexts.Revoked, NameOf(e.Certificate));
            var unknown = chain.ChainElements.FirstOrDefault(e => e.ChainElementStatus.Any(s =>
                s.Status.HasFlag(X509ChainStatusFlags.RevocationStatusUnknown) || s.Status.HasFlag(X509ChainStatusFlags.OfflineRevocation)));
            if (revoked.Count == 0 && unknown != null)
                Add(DescriptorRules.Trust, Severity.Warning, DescriptorTexts.RevocationUnknown, NameOf(unknown.Certificate));
        }

        foreach (var c in elements.Count > 0 ? elements : new List<X509Certificate2> { certificate })
        {
            if (!Policies.CertificateSignatures.Contains(c.SignatureAlgorithm.Value ?? ""))
                Add(DescriptorRules.Policy, Severity.Error, DescriptorTexts.CertificateAlgorithm, NameOf(c),
                    c.SignatureAlgorithm.FriendlyName ?? c.SignatureAlgorithm.Value);
            // Every certificate of the chain, not the signer's alone (Table 3).
            if (!ReferenceEquals(c, elements.FirstOrDefault()))
            {
                using var rsa = c.GetRSAPublicKey();
                if (rsa != null && rsa.KeySize < Policies.MinRsaBits)
                    Add(DescriptorRules.Policy, Severity.Error, DescriptorTexts.CertificateKeyLength, NameOf(c), rsa.KeySize, Policies.MinRsaBits);
            }
            if (CurveOf(c) is { } onCurve && !Policies.Curves.Contains(onCurve))
                Add(DescriptorRules.Policy, Severity.Error, DescriptorTexts.CertificateCurve, NameOf(c), onCurve);
            var outside = signature.Time is { } time && (time < c.NotBefore || time > c.NotAfter);
            if (outside)
                Add(DescriptorRules.Validity, Severity.Error, DescriptorTexts.SignedOutsideValidity, signer, Iso(signature.Time!.Value), NameOf(c),
                    Iso(c.NotBefore), Iso(c.NotAfter));
            else if (now > c.NotAfter)
                Add(DescriptorRules.Validity, Severity.Warning, DescriptorTexts.Expired, NameOf(c), Iso(c.NotAfter), signer);
            if (now < c.NotBefore)
                Add(DescriptorRules.Validity, Severity.Error, DescriptorTexts.NotYetValid, NameOf(c), Iso(c.NotBefore));
        }

        // The trusted certificate itself, byte for byte; a thumbprint is SHA-1.
        var trusted = complete && elements.Any(e => trust.Trusted.Any(t => t.RawData.AsSpan().SequenceEqual(e.RawData)));
        if (complete && !trusted)
            Add(DescriptorRules.Trust, Severity.Error, DescriptorTexts.Untrusted, signer);
        return new SignatureVerdict(signature) { ChainComplete = complete, SignerTrusted = trusted, Chain = elements };
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

    /// <summary>The named curve of an EC key (nistP256, …); "explicit" for a curve given by its parameters; null for another key.</summary>
    private static string? CurveOf(X509Certificate2 certificate)
    {
        using var ec = certificate.GetECDsaPublicKey();
        if (ec == null) return null;
        try
        {
            var curve = ec.ExportParameters(false).Curve;
            if (!curve.IsNamed) return "explicit";
            return curve.Oid.Value switch
            {
                "1.2.840.10045.3.1.7" => "nistP256",
                "1.3.132.0.34" => "nistP384",
                "1.3.132.0.35" => "nistP521",
                var other => curve.Oid.FriendlyName ?? other ?? "unknown",
            };
        }
        catch (CryptographicException) { return "unknown"; }
    }

    /// <summary>
    /// Zip entries that are no part of the package: a name the package library
    /// passes over (no valid part name: "..", a backslash, another spelling of a
    /// part), a second entry of one part, a folder entry with content.
    /// </summary>
    private static List<string> StrayEntries(byte[] bytes, IReadOnlyList<PackagePart> parts)
    {
        var stray = new List<string>();
        var taken = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            using var zip = new ZipArchive(new MemoryStream(bytes, writable: false), ZipArchiveMode.Read);
            foreach (var entry in zip.Entries.OrderBy(e => parts.Any(p => p.Uri.OriginalString == "/" + e.FullName) ? 0 : 1))
            {
                if (entry.FullName == ContentTypesName && taken.Add(ContentTypesName)) continue;
                if (entry.FullName.EndsWith('/') && entry.Length == 0) continue;
                Uri? asPart = null;
                try { asPart = PackUriHelper.CreatePartUri(new Uri("/" + entry.FullName, UriKind.Relative)); }
                catch (Exception ex) when (ex is ArgumentException or UriFormatException) { }
                var part = asPart == null ? null : parts.FirstOrDefault(p => PackUriHelper.ComparePartUri(p.Uri, asPart) == 0);
                if (part == null || !taken.Add(Key(part.Uri))) stray.Add(entry.FullName);
            }
        }
        catch (InvalidDataException) { }
        return stray;
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
        public sealed record SignaturePolicy(string Name, int MinBits, int MaxBits, string? Curve = null);

        public static readonly IReadOnlyDictionary<string, SignaturePolicy> Signature = new Dictionary<string, SignaturePolicy>
        {
            [PackageSignatures.RsaSha256] = new("Rsa-Pkcs-Sha256", 2048, 4096),
            [PackageSignatures.RsaSha384] = new("Rsa-Pkcs-Sha384", 3072, 4096),
            [PackageSignatures.RsaSha512] = new("Rsa-Pkcs-Sha512", 4096, 8192),
            [PackageSignatures.RsaPssSha256] = new("Rsa-Pss-Sha256", 2048, 4096),
            [PackageSignatures.RsaPssSha384] = new("Rsa-Pss-Sha384", 3072, 4096),
            [PackageSignatures.RsaPssSha512] = new("Rsa-Pss-Sha512", 4096, 8192),
            [PackageSignatures.EcdsaSha256] = new("ECC-nistP256", 256, 256, "nistP256"),
            [PackageSignatures.EcdsaSha384] = new("ECC-nistP384", 384, 384, "nistP384"),
            [PackageSignatures.EcdsaSha512] = new("ECC-nistP521", 521, 521, "nistP521"),
        };

        /// <summary>The shortest RSA key a certificate of the chain may have.</summary>
        public const int MinRsaBits = 2048;

        /// <summary>The curves the policies name.</summary>
        public static readonly HashSet<string> Curves = new(StringComparer.Ordinal) { "nistP256", "nistP384", "nistP521" };

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
