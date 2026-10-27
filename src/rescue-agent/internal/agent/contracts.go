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
	ID                 string    `json:"id"`
	Kind               string    `json:"kind"`
	DeviceID           string    `json:"deviceId"`
	DiskFingerprint    string    `json:"diskFingerprint"`
	DiskPath           string    `json:"diskPath"`
	DiskSizeBytes      int64     `json:"diskSizeBytes"`
	LogicalSectorBytes int       `json:"logicalSectorBytes"`
	ImageID            string    `json:"imageId"`
	Challenge          string    `json:"challenge"`
	ExpiresAtUTC       time.Time `json:"expiresAtUtc"`
	Consent            bool      `json:"consent"`
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
}
