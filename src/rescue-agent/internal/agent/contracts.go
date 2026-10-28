package agent

import "time"

const ChunkSize = 4 * 1024 * 1024

type Partition struct {
	Name       string `json:"name"`
	Path       string `json:"path"`
	SizeBytes  int64  `json:"sizeBytes"`
	FileSystem string `json:"fileSystem"`
	MountPoint string `json:"mountPoint"`
}

type Disk struct {
	ID                  string      `json:"id"`
	Path                string      `json:"path"`
	Model               string      `json:"model"`
	Serial              string      `json:"serial"`
	SizeBytes           int64       `json:"sizeBytes"`
	LogicalSectorBytes  int         `json:"logicalSectorBytes"`
	PhysicalSectorBytes int         `json:"physicalSectorBytes"`
	Fingerprint         string      `json:"fingerprint"`
	IsSystem            bool        `json:"isSystem"`
	IsReadOnly          bool        `json:"isReadOnly"`
	Mounted             bool        `json:"mounted"`
	Health              string      `json:"health"`
	Partitions          []Partition `json:"partitions"`
	Layout              *DiskLayout `json:"layout,omitempty"`
	Offline             bool        `json:"-"` // Local WinPE state only; never part of the wire contract.
}

type Inventory struct {
	DeviceID      string    `json:"deviceId"`
	Hostname      string    `json:"hostname"`
	OS            string    `json:"os"`
	Mode          string    `json:"mode"`
	AgentVersion  string    `json:"agentVersion"`
	Capabilities  []string  `json:"capabilities"`
	Disks         []Disk    `json:"disks"`
	CapturedAtUTC time.Time `json:"capturedAtUtc"`
	Notes         []string  `json:"notes"`
}

type Plan struct {
	ID                 string             `json:"id"`
	Kind               string             `json:"kind"`
	DeviceID           string             `json:"deviceId"`
	DiskFingerprint    string             `json:"diskFingerprint"`
	DiskPath           string             `json:"diskPath"`
	DiskSizeBytes      int64              `json:"diskSizeBytes"`
	LogicalSectorBytes int                `json:"logicalSectorBytes"`
	ImageID            string             `json:"imageId"`
	Challenge          string             `json:"challenge"`
	ExpiresAtUTC       time.Time          `json:"expiresAtUtc"`
	Consent            bool               `json:"consent"`
	Clone              *CloneOptions      `json:"clone,omitempty"`
	Deployment         *DeploymentOptions `json:"deployment,omitempty"`
}

type SignedAssignment struct {
	Payload   []byte `json:"payload"`
	Signature []byte `json:"signature"`
}

type ImageChunk struct {
	Sequence    int    `json:"sequence"`
	OffsetBytes int64  `json:"offsetBytes"`
	SizeBytes   int    `json:"sizeBytes"`
	SHA256      string `json:"sha256"`
}

type Manifest struct {
	SchemaVersion      int          `json:"schemaVersion"`
	ID                 string       `json:"id"`
	Name               string       `json:"name"`
	State              string       `json:"state"`
	SourceDeviceID     string       `json:"sourceDeviceId"`
	SourceFingerprint  string       `json:"sourceFingerprint"`
	LogicalSizeBytes   int64        `json:"logicalSizeBytes"`
	LogicalSectorBytes int          `json:"logicalSectorBytes"`
	CreatedAtUTC       time.Time    `json:"createdAtUtc"`
	Chunks             []ImageChunk `json:"chunks"`
	Layout             *DiskLayout  `json:"layout,omitempty"`
}

type CloneOptions struct {
	Strategy              string `json:"strategy"`
	ResizePartitionNumber int    `json:"resizePartitionNumber"`
}

type DeploymentOptions struct {
	MediaID            string `json:"mediaId"`
	ImageIndex         int    `json:"imageIndex"`
	Firmware           string `json:"firmware"`
	RebootAfterInstall bool   `json:"rebootAfterInstall"`
}

type DiskLayout struct {
	TableType      string            `json:"tableType"`
	SectorSize     int               `json:"sectorSize"`
	DiskIdentifier string            `json:"diskIdentifier"`
	FirstUsableLba int64             `json:"firstUsableLba"`
	LastUsableLba  int64             `json:"lastUsableLba"`
	Partitions     []LayoutPartition `json:"partitions"`
}

type LayoutPartition struct {
	Number           int    `json:"number"`
	StartLba         int64  `json:"startLba"`
	LengthLba        int64  `json:"lengthLba"`
	Type             string `json:"type"`
	UUID             string `json:"uuid"`
	Name             string `json:"name"`
	Attributes       string `json:"attributes"`
	Bootable         bool   `json:"bootable"`
	FileSystem       string `json:"fileSystem"`
	MinimumSizeBytes *int64 `json:"minimumSizeBytes,omitempty"`
}

type RestoreReport struct {
	Strategy             string      `json:"strategy"`
	DataVerified         bool        `json:"dataVerified"`
	TableVerified        bool        `json:"tableVerified"`
	BootPayloadPreserved bool        `json:"bootPayloadPreserved"`
	BootVerified         bool        `json:"bootVerified"`
	BytesWritten         int64       `json:"bytesWritten"`
	SourceLayout         *DiskLayout `json:"sourceLayout,omitempty"`
	TargetLayout         *DiskLayout `json:"targetLayout,omitempty"`
	Warnings             []string    `json:"warnings"`
}
