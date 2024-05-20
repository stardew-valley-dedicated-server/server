package util

import (
	"strings"
)

func GetBucketAndObjectFromGcsPath(path string) (string, string) {
	pathWoGS := strings.Split(path, "//")[1]  // junimo-backups/078338ba-6465-43a4-a045-4a6f7df674fc/1648081818
	pathParts := strings.Split(pathWoGS, "/") // [junimo-backups, 078338ba-6465-43a4-a045-4a6f7df674fc, 1648081818]
	bucketName := pathParts[0]
	objectName := strings.Join(pathParts[1:], "/")
	return bucketName, objectName
}
