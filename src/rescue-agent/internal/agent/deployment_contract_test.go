package agent

import (
	"context"
	"crypto"
	"crypto/rand"
	"crypto/rsa"
	"crypto/sha256"
	"encoding/json"
	"io"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

func TestDeploymentContractRejectsUntypedOrUnconfirmedPlans(t *testing.T) {
	key, cert, _ := testCertificate(t)
	p := Plan{ID: "deploy-1", Kind: "DEPLOY_WINDOWS", DeviceID: "device", DiskFingerprint: "fingerprint", DiskPath: `\\.\PhysicalDrive1`, DiskSizeBytes: 256 * 1024 * 1024 * 1024, LogicalSectorBytes: 512, Challenge: "CONFIRM-DESTINATION", Consent: true, ExpiresAtUTC: time.Now().Add(time.Hour), Deployment: &DeploymentOptions{MediaID: "media-1", ImageIndex: 1, Firmware: "GPT_UEFI"}}
	check := func(p Plan) error {
		payload, _ := json.Marshal(p)
		hash := sha256.Sum256(payload)
		signature, err := rsa.SignPKCS1v15(rand.Reader, key, crypto.SHA256, hash[:])
		if err != nil {
			t.Fatal(err)
		}
		_, err = VerifyAssignment(SignedAssignment{Payload: payload, Signature: signature}, cert, "device", time.Now())
		return err
	}
	if err := check(p); err != nil {
		t.Fatal(err)
	}
	p.Deployment = nil
	if err := check(p); err == nil {
		t.Fatal("deploy sem perfil aceito")
	}
	p.Deployment = &DeploymentOptions{MediaID: "../media", ImageIndex: 1, Firmware: "GPT_UEFI"}
	if err := check(p); err == nil {
		t.Fatal("mídia com traversal aceita")
	}
	p.Deployment.MediaID = "media-1"
	p.Deployment.Firmware = "SHELL"
	if err := check(p); err == nil {
		t.Fatal("firmware arbitrário aceito")
	}
	p.Deployment.Firmware = "MBR_BIOS"
	p.Challenge = ""
	if err := check(p); err == nil {
		t.Fatal("deploy sem confirmação aceito")
	}
}

func TestEnrollmentModesNeverReuseAnotherAgentIdentity(t *testing.T) {
	if _, _, err := EnrollMode(context.Background(), nil, t.TempDir(), io.Discard, "UNKNOWN"); err == nil {
		t.Fatal("modo desconhecido aceito")
	}
	dir := t.TempDir()
	data, _ := json.Marshal(credentials{DeviceID: "rescue-device", Mode: "RESCUE"})
	if err := os.WriteFile(filepath.Join(dir, "credentials.json"), data, 0600); err != nil {
		t.Fatal(err)
	}
	if _, _, err := EnrollMode(context.Background(), nil, dir, io.Discard, "WINPE"); err == nil || !strings.Contains(err.Error(), "outro modo") {
		t.Fatalf("identidade Rescue aceita como WinPE: %v", err)
	}
	dir = t.TempDir()
	data, _ = json.Marshal(enrollment{ID: "rescue-enroll", Mode: "RESCUE", ExpiresAtUTC: time.Now().Add(time.Minute)})
	if err := os.WriteFile(filepath.Join(dir, "enrollment.json"), data, 0600); err != nil {
		t.Fatal(err)
	}
	if _, _, err := EnrollMode(context.Background(), nil, dir, io.Discard, "WINPE"); err == nil || !strings.Contains(err.Error(), "outro modo") {
		t.Fatalf("pareamento Rescue aceito como WinPE: %v", err)
	}
}
