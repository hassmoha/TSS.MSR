﻿/* 
 *  Copyright (c) Microsoft Corporation. All rights reserved.
 *  Licensed under the MIT License. See the LICENSE file in the project root for full license information.
 */

using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.Pkcs;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Asn1.X9;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Operators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.X509;
using System;
using System.Diagnostics;

using Tpm2Lib;
using Tpm2Tester;

namespace Tpm2TestSuite
{
    /// <summary>
    /// Contains static methods supporting the TPM2_CertifyX509() command
    /// </summary>
    static class X509Helpers
    {
        /// <summary>
        /// Makes a minimal partial certificate for a signing key
        /// </summary>
        /// <returns></returns>
        internal static PartialCertificate
            MakePartialCert(TpmPublic pub, string issuer = null, string subject = null,
                            DateTime? notBefore = null, DateTime? notAfter = null)
        {
            return MakePartialCert(pub.objectAttributes, pub.type, TpmHelper.GetSchemeHash(pub), issuer, subject, notBefore, notAfter);
        }

        /// <summary>
        /// Makes a minimal partial certificate for a signing key
        /// </summary>
        /// <returns></returns>
        internal static PartialCertificate
            MakePartialCert(ObjectAttr keyAttrs,
                            TpmAlgId keyType = TpmAlgId.None, TpmAlgId schemeHash = TpmAlgId.None,
                            string issuer = null, string subject = null,
                            DateTime? notBefore = null, DateTime? notAfter = null)
        {
            AlgorithmIdentifier algID = null;
            if (keyType != TpmAlgId.None && schemeHash != TpmAlgId.None)
                algID = GetAlgId(keyType, schemeHash);
            return MakePartialCert(GetKeyUsage(keyAttrs), algID, issuer, subject, notBefore, notAfter);
        }

        /// <summary>
        /// Makes a minimal partial certificate for a signing key
        /// </summary>
        /// <returns></returns>
        internal static PartialCertificate
            MakePartialCert(X509KeyUsage keyUsage, AlgorithmIdentifier algID = null,
                            string issuer = null, string subject = null,
                            DateTime? notBefore = null, DateTime? notAfter = null)
        {
            X509ExtensionsGenerator g = new X509ExtensionsGenerator();
            g.AddExtension(X509Extensions.KeyUsage, true, keyUsage);

            return new PartialCertificate()
            {
                Issuer = new X509Name(issuer ?? "CN=TPM Test Issuer,O=TPM Test Suite"),
                NotBefore = notBefore ?? new DateTime(2000, 1, 1),
                NotAfter = notAfter ?? new DateTime(2999, 12, 31),
                Subject = new X509Name(subject ?? "CN=TPM X509 CA,O=MSFT"),
                SigAlgID = algID,
                Extensions = g.Generate()
            };
        }

        internal static X509KeyUsage GetKeyUsage(ObjectAttr keyAttrs)
        {
            int usage = 0;
            if (keyAttrs.HasFlag(ObjectAttr.Sign))
                usage |= X509KeyUsage.DigitalSignature;
            if (keyAttrs.HasFlag(ObjectAttr.Decrypt))
                if (keyAttrs.HasFlag(ObjectAttr.Restricted))
                    usage |= X509KeyUsage.KeyEncipherment;
                else
                    usage |= X509KeyUsage.DataEncipherment;
            if (keyAttrs.HasFlag(ObjectAttr.FixedTPM))
                usage |= X509KeyUsage.NonRepudiation;
            return new X509KeyUsage(usage);
        }

        internal static AlgorithmIdentifier GetAlgId(TpmAlgId keyType, TpmAlgId schemeHash)
        {
            DerObjectIdentifier doid = null;
            if (keyType == TpmAlgId.Rsa)
            {
                switch (schemeHash)
                {
                    case TpmAlgId.Sha1: doid = PkcsObjectIdentifiers.Sha1WithRsaEncryption; break;
                    case TpmAlgId.Sha256: doid = PkcsObjectIdentifiers.Sha256WithRsaEncryption; break;
                    case TpmAlgId.Sha384: doid = PkcsObjectIdentifiers.Sha384WithRsaEncryption; break;
                    case TpmAlgId.Sha512: doid = PkcsObjectIdentifiers.Sha512WithRsaEncryption; break;
                    default: return null;
                }
            }
            else if (keyType == TpmAlgId.Ecc)
            {
                switch (schemeHash)
                {
                    case TpmAlgId.Sha1: doid = X9ObjectIdentifiers.ECDsaWithSha1; break;
                    case TpmAlgId.Sha256: doid = X9ObjectIdentifiers.ECDsaWithSha256; break;
                    case TpmAlgId.Sha384: doid = X9ObjectIdentifiers.ECDsaWithSha384; break;
                    case TpmAlgId.Sha512: doid = X9ObjectIdentifiers.ECDsaWithSha512; break;
                    default: return null;
                }
            }
            else
                return null;
            return new AlgorithmIdentifier(doid);
        }

        /// <summary>
        /// To be called after the TPM has returned the addedTo part of the certificate and the signature.  Assembles
        /// partialCert, addedTo, and the signature to form  an actual X.509 certificate encapsulated in a Bouncy Castle
        /// X509Certificate
        /// </summary>
        /// <param name="partialCert"></param>
        /// <param name="addedToCertificate"></param>
        /// <param name="signature"></param>
        /// <returns></returns>
        internal static X509Certificate AssembleCertificate(
            PartialCertificate partialCert, AddedToCertificate addedToCertificate,
            byte[] signatureBytes)
        {
            Debug.Assert(addedToCertificate.Version.ToString() == "2");
            var signature = new DerBitString(signatureBytes);
            var sigAlgID = partialCert.SigAlgID ?? addedToCertificate.SigAlgID;

            // Assemble TBS.  Start with a vector which we will later convert to a sequence
            Asn1EncodableVector tbsContents = new Asn1EncodableVector();
            tbsContents.Add(new DerTaggedObject(0, new DerInteger(BigInteger.ValueOf(2))));
            tbsContents.Add(addedToCertificate.SerialNumber);
            tbsContents.Add(sigAlgID);
            tbsContents.Add(partialCert.Issuer);
            tbsContents.Add(new CertificateValidity(partialCert.NotBefore, partialCert.NotAfter));
            tbsContents.Add(partialCert.Subject);
            tbsContents.Add(addedToCertificate.SubjectPublicKeyInfo);
            // Add optional components
            if (partialCert.IssuerUniqueId != null) tbsContents.Add(new DerTaggedObject(false, 1, new DerBitString(partialCert.IssuerUniqueId)));
            if (partialCert.SubjectUniqueId != null) tbsContents.Add(new DerTaggedObject(false, 2, new DerBitString(partialCert.SubjectUniqueId)));
            if (partialCert.Extensions != null) tbsContents.Add(new DerTaggedObject(3, partialCert.Extensions.ToAsn1Object()));

            // Convert the vector to an ASN SEQUENCE
            var tbsCertificate = new DerSequence(tbsContents);

            // assemble the certificate.  Start with the components as a vector
            Asn1EncodableVector certContents = new Asn1EncodableVector();
            certContents.Add(tbsCertificate);
            certContents.Add(sigAlgID);
            certContents.Add(signature);

            // Convert to a SEQUENCE.  certSequence will contain the DER encoded certificate
            var certSequence = new DerSequence(certContents);

            // Convert to a first-class BC X509Certificate
            var certBytes = certSequence.GetEncoded();
            var cert = new X509CertificateParser().ReadCertificate(certSequence.GetEncoded());

            // and return it
            return cert;
        }

        /// <summary>
        /// Simulates the crytographic operations that a TPM would perform in X509Certify()
        /// (Mostly to test the tester)
        /// </summary>
        /// <param name="partialCert"></param>
        /// <param name="publicKeyToCertify"></param>
        /// <param name="signingKey"></param>
        /// <returns>Tuple (X509Certificate CompleteCertificate, AddedToCertificate AddedTo)</returns>
        internal static (X509Certificate, AddedToCertificate)
            SimulateX509Certify(PartialCertificate partialCert, AsymmetricKeyParameter publicKeyToCertify,
                                AsymmetricKeyParameter signingKey, string signingAlgorithm)
        {

            // We can simulate ECDSA/SHA256 and RSA/SHA256

            ISignatureFactory signatureFactory;
            DerObjectIdentifier sigAlgOid;
            if (signingKey is ECPrivateKeyParameters)
            {
                sigAlgOid = X9ObjectIdentifiers.ECDsaWithSha256;
            }
            else
            {
                sigAlgOid = PkcsObjectIdentifiers.Sha256WithRsaEncryption;
            }


            //signatureFactory = new Asn1SignatureFactory(sigAlgOid.ToString(), signingKey);
            signatureFactory = new Asn1SignatureFactory(sigAlgOid.ToString(), signingKey);

            // Make the certificate
            var certGenerator = new X509V3CertificateGenerator();
            certGenerator.SetIssuerDN(partialCert.Issuer);
            certGenerator.SetSubjectDN(partialCert.Subject);
            certGenerator.SetSerialNumber(BigInteger.ValueOf(1));
            certGenerator.SetNotBefore(partialCert.NotBefore);
            certGenerator.SetNotAfter(partialCert.NotAfter);
            certGenerator.SetPublicKey(publicKeyToCertify);
            if (partialCert.SubjectUniqueId != null) certGenerator.SetSubjectUniqueID(ByteArrayToBoolArray(partialCert.SubjectUniqueId));
            if (partialCert.IssuerUniqueId != null) certGenerator.SetIssuerUniqueID(ByteArrayToBoolArray(partialCert.IssuerUniqueId));

            // process the extensions.  Note that this will not preserve the order 
            var extensions = partialCert.Extensions;
            if (extensions != null)
            {
                foreach (var critExtOid in extensions.GetCriticalExtensionOids())
                {
                    certGenerator.AddExtension(critExtOid.ToString(), true, extensions.GetExtension(critExtOid).GetParsedValue());
                }
                foreach (var nonCritExtOid in extensions.GetNonCriticalExtensionOids())
                {
                    certGenerator.AddExtension(nonCritExtOid.ToString(), false, extensions.GetExtension(nonCritExtOid).GetParsedValue());
                }
            }

            // and sign to make the cert
            var cert = certGenerator.Generate(signatureFactory);

            // take things apart again for addedToCert
            var subjectPubKeyInfo = SubjectPublicKeyInfoFactory.CreateSubjectPublicKeyInfo(publicKeyToCertify);

            // get the exact bytes for the signature algorithm
            var tbsSequence = ((DerSequence)DerSequence.FromByteArray(cert.GetTbsCertificate()));
            var sigAlgBytes = tbsSequence[2];

            AddedToCertificate addedTo = new AddedToCertificate()
            {
                Version = new DerInteger(BigInteger.ValueOf(cert.Version)),
                SerialNumber = new DerInteger(cert.SerialNumber),
                SigAlgID = AlgorithmIdentifier.GetInstance(sigAlgBytes),
                SubjectPublicKeyInfo = subjectPubKeyInfo
            };
            return (cert, addedTo);
        }


        /// <summary>
        /// Validate the tester by emulating the actions of the TPM
        /// </summary>
        internal static void TestTester()
        {
            var random = new SecureRandom();

            // Algorithms to test
            var algInfo = new[] {
                new { genner = (IAsymmetricCipherKeyPairGenerator) new ECKeyPairGenerator(), strength = 256, signingAlgorithm = "SHA256WITHECDSA" },
                new { genner = (IAsymmetricCipherKeyPairGenerator) new RsaKeyPairGenerator(), strength = 2048, signingAlgorithm = "SHA256WITHRSAENCRYPTION"}
            };

            foreach (var keyType in algInfo)
            {
                // Make a two keys of the specified algorithm
                var keyGenerationParameters = new KeyGenerationParameters(random, keyType.strength);
                keyType.genner.Init(keyGenerationParameters);

                var signingKey = keyType.genner.GenerateKeyPair();
                var toBeCertifiedKeyPair = keyType.genner.GenerateKeyPair();

                // Make the partial certificate that is input to the TPM
                PartialCertificate partialCert = MakePartialCert(ObjectAttr.Sign,
                                                    keyType.genner is ECKeyPairGenerator ? TpmAlgId.Ecc : TpmAlgId.Rsa, TpmAlgId.Sha256);

                // Simulate the actions of the TPM.  This returns both the full and partial (AddedTo) certificate
                // The full certitificate is just for debugging
                var certTuple /*(CompleteCertificate, AddedTo)*/ =
                        SimulateX509Certify(partialCert, toBeCertifiedKeyPair.Public, signingKey.Private, keyType.signingAlgorithm);
                X509Certificate CompleteCertificate = certTuple.Item1;
                AddedToCertificate AddedTo = certTuple.Item2;

                // Is the full certificate OK (it certainly should be)
                CompleteCertificate.Verify(signingKey.Public);
                // Debugging help...
                DebugPrintHex(CompleteCertificate.GetEncoded(), "ActualCert");

                // When using TPM2_CertifyX509() the TPM does not return the whole certificate: it returns the 
                // parts of the certificate that were not originally provided in partialCertificate in a data
                // structure called AddedTo, as well as the signature.  The caller/TSS has to recosntruct 
                // the full certificate.

                var signature = CompleteCertificate.GetSignature();
                var finishedCert = AssembleCertificate(partialCert, AddedTo, signature);
                DebugPrintHex(finishedCert.GetEncoded(), "AssembledCert");

                // sanity check that we can parse a DER encoded AddedTo (this is wha the TPM will return.)
                var addedToBytes = AddedTo.GetDerEncoded();
                var reconstructedAddedTo = AddedToCertificate.FromDerEncoding(addedToBytes);
                AssertByteArraysTheSame(addedToBytes, reconstructedAddedTo.ToAsn1Object().GetDerEncoded());

                // Sanity
                AssertByteArraysTheSame(CompleteCertificate.GetEncoded(), finishedCert.GetEncoded());

                // and more sanity
                finishedCert.Verify(signingKey.Public);
            }
        }

        /// <summary>
        /// Debugging support
        /// </summary>
        /// <param name="obj"></param>
        /// <param name="tag"></param>
        public static void DebugPrintHex(Asn1Encodable obj, string tag = "")
        {
            DebugPrintHex(obj.GetDerEncoded(), tag);
        }

        /// <summary>
        /// Debugging support
        /// </summary>
        /// <param name="bin"></param>
        /// <param name="tag"></param>
        public static void DebugPrintHex(byte[] bin, string tag = "")
        {
            Debug.WriteLine(tag);
            foreach (var b in bin)
            {
                Debug.Write(b.ToString("X2"));
            }
            Debug.WriteLine("");
        }

        /// <summary>
        /// Deugging support
        /// </summary>
        /// <param name="x0"></param>
        /// <param name="x1"></param>
        public static void AssertByteArraysTheSame(byte[] x0, byte[] x1)
        {
            var min = Math.Min(x0.Length, x1.Length);
            if (x0.Length != x1.Length)
            {
                Debug.WriteLine($"Arrays not the same length {x0.Length}, {x1.Length}");
            }
            Debug.Assert(x0.Length == x1.Length);

            for (int j = 0; j < min; j++)
            {
                Debug.Assert(x0[j] == x1[j]);
            }

        }

        /// <summary>
        /// The Bouncy Castle certificate generator needs a bool[] for Subject and Issuer UniqueIdentifier
        /// </summary>
        /// <param name="x"></param>
        /// <returns></returns>
        static bool[] ByteArrayToBoolArray(byte[] x)
        {
            var ret = new bool[x.Length * 8];
            for (int j = 0; j < x.Length; j++)
            {
                for (int bit = 0; bit < 7; bit++)
                {
                    int bitPos = 7 - bit;
                    byte bitVal = (byte)((byte)1 << bitPos);
                    if ((x[j] & bitVal) == 0) continue;
                    ret[j] = true;
                }
            }
            return ret;
        }
    } // static class X509Helpers

    /// <summary>
    /// A DER-encoded PartialCertificate must be input to TPM2_X509Certify()
    /// </summary>
    public class PartialCertificate : Asn1Encodable
    {
        public X509Name Issuer;
        public DateTime NotBefore;
        public DateTime NotAfter;
        public X509Name Subject;
        public AlgorithmIdentifier SigAlgID;
        public byte[] IssuerUniqueId; // todo: this won't work for non-octets (i.e. padding!=0)
        public byte[] SubjectUniqueId;
        public X509Extensions Extensions;

        public override Asn1Object ToAsn1Object()
        {
            // Construct a Asn1Vector with the mandadory and optional contents
            Asn1EncodableVector partialCertContents = new Asn1EncodableVector();
            if (SigAlgID != null)
                partialCertContents.Add(SigAlgID);
            partialCertContents.Add(Issuer);
            partialCertContents.Add(new CertificateValidity(NotBefore, NotAfter).ToAsn1Object());
            partialCertContents.Add(Subject);

            if (IssuerUniqueId != null) partialCertContents.Add(new DerTaggedObject(1, new DerBitString(IssuerUniqueId)));
            if (SubjectUniqueId != null) partialCertContents.Add(new DerTaggedObject(2, new DerBitString(SubjectUniqueId)));
            partialCertContents.Add(new DerTaggedObject(3, Extensions));

            // Encode it as a DER sequence 
            var partialCert = new DerSequence(partialCertContents);
            return partialCert;
        }
    }


    /// <summary>
    /// TPM2_CertifyX509() returns an AddedToCertificate and the actual signature.  The completed certificate must
    /// be assembled by the caller.
    /// </summary>
    public class AddedToCertificate : Asn1Encodable
    {
        public DerInteger Version;
        public DerInteger SerialNumber;
        public AlgorithmIdentifier SigAlgID;
        public SubjectPublicKeyInfo SubjectPublicKeyInfo;

        public override Asn1Object ToAsn1Object()
        {
            Asn1EncodableVector addeddToContents = new Asn1EncodableVector();
            var addedElts = SigAlgID == null
                          ? new Asn1Encodable[] { new DerTaggedObject(0, Version), SerialNumber, SubjectPublicKeyInfo }
                          : new Asn1Encodable[] { new DerTaggedObject(0, Version), SerialNumber, SigAlgID, SubjectPublicKeyInfo };
            addeddToContents.Add(addedElts);
            var addedToSequence = new DerSequence(addeddToContents);
            return addedToSequence;
        }

        /// <summary>
        /// Parses the AddedTo data that is returned from TPM2_CertifyX509()
        /// </summary>
        /// <param name="data"></param>
        /// <returns></returns>
        public static AddedToCertificate FromDerEncoding(byte[] data)
        {
            var ret = new AddedToCertificate();
            var sequence = (DerSequence)DerSequence.FromByteArray(data);
            var taggedVersion = (DerTaggedObject)(sequence[0]);
            Debug.Assert(taggedVersion.TagNo == 0);
            ret.Version = (DerInteger)taggedVersion.GetObject();
            ret.SerialNumber = (DerInteger)sequence[1];
            int i = 2;
            if (sequence.Count > 3)
                ret.SigAlgID = AlgorithmIdentifier.GetInstance(sequence[i++]);
            ret.SubjectPublicKeyInfo = SubjectPublicKeyInfo.GetInstance(sequence[i++]);
            return ret;
        }
    } // class AddedToCertificate

    public class CertificateValidity : Asn1Encodable
    {
        public DateTime NotBefore;
        public DateTime NotAfter;

        public CertificateValidity(DateTime notBefore, DateTime notAfter)
        {
            NotBefore = notBefore;
            NotAfter = notAfter;
        }
        public override Asn1Object ToAsn1Object()
        {
            return new DerSequence(new Time(NotBefore), new Time(NotAfter));
        }
    }
}
