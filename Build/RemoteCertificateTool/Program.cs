using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

try {
if (args.Length!=5 || (args[0]!="server" && args[0]!="client")) {
	Console.Error.WriteLine("Usage: RemoteCertificateTool <server|client> <name> <pfx-path> <cer-path> <password-file>");
	return 2;
}
var isServer=args[0]=="server"; var name=args[1];
using var key=RSA.Create(3072);
var request=new CertificateRequest($"CN=dgSpy {(isServer ? "Gateway" : "host "+name)}",key,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false,false,0,true));
request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature|X509KeyUsageFlags.KeyEncipherment,true));
var eku=new OidCollection { new Oid(isServer ? "1.3.6.1.5.5.7.3.1" : "1.3.6.1.5.5.7.3.2") };
request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(eku,true));
if (isServer) {
	var san=new SubjectAlternativeNameBuilder();
	if (IPAddress.TryParse(name,out var address)) san.AddIpAddress(address); else san.AddDnsName(name);
	request.CertificateExtensions.Add(san.Build());
}
using var certificate=request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5),DateTimeOffset.UtcNow.AddYears(5));
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[2]))!);
var password=File.ReadAllText(args[4]).Trim();
if (password.Length==0) throw new InvalidOperationException("The PFX password file is empty.");
// Use the Windows-compatible PKCS#12 profile explicitly. Newer runtimes otherwise emit
// AES-encrypted PFX files that older Windows/.NET deployment targets may reject.
var pbe=new PbeParameters(PbeEncryptionAlgorithm.TripleDes3KeyPkcs12,HashAlgorithmName.SHA1,2000);
File.WriteAllBytes(args[2],certificate.ExportPkcs12(pbe,password));
File.WriteAllBytes(args[3],certificate.Export(X509ContentType.Cert));
return 0;
}
catch(Exception ex) {
	var fatalLog=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"dgSpy","certificate-tool-fatal.log");
	var detail=$"[{DateTime.UtcNow:O}] {ex}\n"; var saved=false; try { Directory.CreateDirectory(Path.GetDirectoryName(fatalLog)!); File.AppendAllText(fatalLog,detail); saved=true; } catch { }
	Console.Error.WriteLine(saved ? $"Certificate generation failed: {ex.Message}. Details: {fatalLog}" : $"Certificate generation failed: {ex}"); return 1;
}
