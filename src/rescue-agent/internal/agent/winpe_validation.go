package agent

import (
	"bytes"
	"context"
	"crypto/sha256"
	"encoding/binary"
	"encoding/hex"
	"errors"
	"io"
)

// Read the persisted staging file again before any destination operation. The
// transfer hash alone would not catch a damaged scratch file or short write.
func verifyWinPEMediaFile(ctx context.Context, file io.Reader, media DeploymentMedia) error {
	buffer := make([]byte, ChunkSize)
	whole := sha256.New()
	for i, chunk := range media.Chunks {
		if err := ctx.Err(); err != nil {
			return err
		}
		if chunk.SizeBytes < 1 || chunk.SizeBytes > len(buffer) {
			return errors.New("chunk de mídia inválido")
		}
		data := buffer[:chunk.SizeBytes]
		if _, err := io.ReadFull(file, data); err != nil {
			return errors.New("mídia persistida truncada")
		}
		if i == 0 && (len(data) < 8 || !bytes.Equal(data[:8], []byte{'M', 'S', 'W', 'I', 'M', 0, 0, 0})) {
			return errors.New("cabeçalho MSWIM inválido")
		}
		hash := sha256.Sum256(data)
		if hex.EncodeToString(hash[:]) != chunk.SHA256 {
			return errors.New("mídia persistida diverge do hash de chunk")
		}
		whole.Write(data)
	}
	var extra [1]byte
	if n, err := file.Read(extra[:]); n != 0 || err != io.EOF {
		return errors.New("mídia persistida possui bytes adicionais ou erro de leitura")
	}
	if hex.EncodeToString(whole.Sum(nil)) != media.SHA256 {
		return errors.New("mídia persistida diverge do hash completo")
	}
	return nil
}

type pePartition struct {
	number        uint32
	start, length int64
	kind          []byte
	active        bool
}

// DRIVE_LAYOUT_INFORMATION_EX and PARTITION_INFORMATION_EX are the native x64
// layouts documented by Microsoft. No disk is opened by these decoding helpers.
func decodeWinPELayout(data []byte, firmware string) ([]pePartition, error) {
	if len(data) < 48 {
		return nil, errors.New("layout Windows truncado")
	}
	style := binary.LittleEndian.Uint32(data)
	count := int(binary.LittleEndian.Uint32(data[4:]))
	expectedStyle := uint32(1)
	if firmware == "MBR_BIOS" {
		expectedStyle = 0
	} else if firmware != "GPT_UEFI" {
		return nil, errors.New("firmware inválido")
	}
	if style != expectedStyle || count < 1 || count > 256 || 48+count*144 > len(data) {
		return nil, errors.New("tabela de partições não corresponde ao plano")
	}
	partitions := []pePartition{}
	var end int64
	for i := 0; i < count; i++ {
		entry := data[48+i*144 : 48+(i+1)*144]
		start, length := int64(binary.LittleEndian.Uint64(entry[8:])), int64(binary.LittleEndian.Uint64(entry[16:]))
		if length == 0 {
			continue
		} // Unused basic MBR partition slots.
		if binary.LittleEndian.Uint32(entry) != style || start < 1024*1024 || length < 1 || start < end || length > int64(^uint64(0)>>1)-start || start%(1024*1024) != 0 {
			return nil, errors.New("limites de partições inválidos")
		}
		kind := append([]byte{}, entry[32:48]...)
		partitions = append(partitions, pePartition{binary.LittleEndian.Uint32(entry[24:]), start, length, kind, entry[33] != 0})
		end = start + length
	}
	if firmware == "GPT_UEFI" {
		efi := []byte{0x28, 0x73, 0x2a, 0xc1, 0x1f, 0xf8, 0xd2, 0x11, 0xba, 0x4b, 0x00, 0xa0, 0xc9, 0x3e, 0xc9, 0x3b}
		msr := []byte{0x16, 0xe3, 0xc9, 0xe3, 0x5c, 0x0b, 0xb8, 0x4d, 0x81, 0x7d, 0xf9, 0x2d, 0xf0, 0x02, 0x15, 0xae}
		basic := []byte{0xa2, 0xa0, 0xd0, 0xeb, 0xe5, 0xb9, 0x33, 0x44, 0x87, 0xc0, 0x68, 0xb6, 0xb7, 0x26, 0x99, 0xc7}
		if len(partitions) != 3 || !bytes.Equal(partitions[0].kind, efi) || !bytes.Equal(partitions[1].kind, msr) || !bytes.Equal(partitions[2].kind, basic) || partitions[0].length != 260*1024*1024 || partitions[1].length != 16*1024*1024 {
			return nil, errors.New("EFI/MSR/Windows criados divergem do layout aprovado")
		}
	} else if len(partitions) != 2 || partitions[0].kind[0] != 7 || partitions[1].kind[0] != 7 || !partitions[0].active || partitions[0].length != 550*1024*1024 {
		return nil, errors.New("sistema ativo/Windows criados divergem do layout aprovado")
	}
	for i, p := range partitions {
		if p.number != uint32(i+1) {
			return nil, errors.New("numeração de partições inesperada")
		}
	}
	if partitions[len(partitions)-1].length < 1024*1024*1024 {
		return nil, errors.New("partição Windows insuficiente")
	}
	return partitions, nil
}
