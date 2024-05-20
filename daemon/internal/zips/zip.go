package zips

import (
	"archive/zip"
	"go.uber.org/zap"
	"io"
	"os"
	"path/filepath"
)

const bufferSize = 5120

func ExtractZipToPath(targetPath string, zipReader *zip.Reader) []string {
	topLevelDirectories := make([]string, 0)
	buffer := make([]byte, bufferSize)
	targetTopDir := filepath.Base(targetPath)
	for _, f := range zipReader.File {
		// if this is a directory don't unzip it
		if f.FileInfo().IsDir() {
			pathList := filepath.SplitList(f.Name)
			// is this a top level directory?
			if len(pathList) >= 2 && pathList[len(pathList)-2] == targetTopDir {
				topLevelDirectories = append(topLevelDirectories, f.Name)
			}
			continue
		}
		targetFileName := filepath.Join(targetPath, f.Name)
		zap.S().Infof("Unzipping: %v to %v", f.Name, targetFileName)

		targetFile, err := createFileWithDirs(targetFileName)
		if err != nil {
			zap.S().Errorf("Failed to create: %v, reason: %v", targetFileName, err)
			continue
		}

		fileContents, err := f.Open()
		if err != nil {
			zap.S().Errorf("Failed to open zip file: %v, reason: %v", f.Name, err)
			continue
		}

		_, err = io.CopyBuffer(targetFile, fileContents, buffer)
		if err != nil {
			zap.S().Errorf("Failed to copy zip file contents to disk: %v, reason: %v", f.Name, err)
			fileContents.Close()
			continue
		}

		fileContents.Close()
	}
	return topLevelDirectories
}

func createFileWithDirs(p string) (*os.File, error) {
	if err := os.MkdirAll(filepath.Dir(p), 0666); err != nil {
		return nil, err
	}
	return os.Create(p)
}
