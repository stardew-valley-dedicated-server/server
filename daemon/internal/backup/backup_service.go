package backup

import (
	"archive/zip"
	"bytes"
	"context"
	"encoding/json"
	"io"
	"io/fs"
	"io/ioutil"
	"os"
	"path/filepath"
	"strconv"
	"time"

	"cloud.google.com/go/storage"
	pbsd "github.com/junimohost/game-daemon/gen/proto/go/junimohost/stardewdaemon/v1"
	"github.com/junimohost/game-daemon/internal/util"
	"github.com/junimohost/game-daemon/internal/zips"
	"github.com/junimohost/game-daemon/pkg/stardewtypes"
	"go.uber.org/zap"
)

const (
	GameDataPath           = "/config/xdg/config/StardewValley"
	StartupPreferencesPath = GameDataPath + "/startup_preferences"
	SavesPath              = GameDataPath + "/Saves"
	SmapiDataPath          = GameDataPath + "/.smapi"
	GameLoaderJsonPath     = SmapiDataPath + "/mod-data/junimohost.server/junimohost.gameloader.json"

	OldSaveGameInfo = "SaveGameInfo_old"
)

type Service struct {
	storageClient       *storage.Client
	stardewDaemonClient pbsd.StardewDaemonServiceClient
	backupBucketName    string
	serverID            string
	backendAvailable    bool
}

func NewService(storageClient *storage.Client, stardewDaemonClient pbsd.StardewDaemonServiceClient, backupBucketName string, serverID string, backendAvailable bool) *Service {
	return &Service{storageClient: storageClient, stardewDaemonClient: stardewDaemonClient, backupBucketName: backupBucketName, serverID: serverID, backendAvailable: backendAvailable}
}

func (s *Service) CreateBackup(ctx context.Context, date stardewtypes.SDate) error {
	objectName, err := s.uploadBackupZipToGCS(ctx)
	if err != nil {
		return err
	}

	if s.backendAvailable {
		_, err = s.stardewDaemonClient.IndexBackup(ctx, &pbsd.IndexBackupRequest{
			ServerId:      s.serverID,
			GcsPath:       "gs://" + s.backupBucketName + "/" + objectName,
			StardewYear:   int64(date.Year),
			StardewSeason: pbsd.StardewSeasons(date.Season),
			StardewDay:    int64(date.Day),
		})
		if err != nil {
			return err
		}
	}

	return nil

}

func (s *Service) RestoreBackup(ctx context.Context, backupPath string) error {

	bucketName, objectName := util.GetBucketAndObjectFromGcsPath(backupPath)
	gcsR, err := s.storageClient.Bucket(bucketName).Object(objectName).NewReader(ctx)
	if err != nil {
		zap.S().Errorf("Failed to find backup: %v", err)
		return err
	}
	defer gcsR.Close()

	backupFileBuffer, err := ioutil.ReadAll(gcsR) //backup size is not going to be larger than 20-30MB (with mods)
	if err != nil {
		zap.S().Errorf("Failed to download backup: %v", err)
		return err
	}

	// remove localfiles
	err = s.deleteCurrentGameFiles(err)
	if err != nil {
		return err
	}

	// unzip
	backupFileReader := bytes.NewReader(backupFileBuffer)
	zipReader, err := zip.NewReader(backupFileReader, int64(len(backupFileBuffer)))
	if err != nil {
		zap.S().Errorf("Failed to open zip: %v", err)
		return err
	}

	zips.ExtractZipToPath(GameDataPath, zipReader)
	return nil
}

func (s *Service) deleteCurrentGameFiles(err error) error {
	// just get direct children and let RemoveAll handle sub dirs
	filesToDelete, err := filepath.Glob(GameDataPath + "/*")
	if err != nil {
		zap.S().Errorf("Failed to Glob files: %v", err)
		return err
	}

	for _, file := range filesToDelete {
		if err := os.RemoveAll(file); err != nil {
			zap.S().Errorf("Failed to clear game files: %v", err)
			return err
		}
	}
	return nil
}

func (s *Service) uploadBackupZipToGCS(ctx context.Context) (string, error) {
	objectName := s.serverID + "/" + strconv.Itoa(int(time.Now().Unix()))
	gcsW := s.storageClient.Bucket(s.backupBucketName).Object(objectName).NewWriter(ctx)
	defer gcsW.Close()

	err := s.createBackupZip(gcsW)
	if err != nil {
		return "", err
	}

	return objectName, nil
}

func (s *Service) createBackupZip(writer io.Writer) error {
	zipW := zip.NewWriter(writer)
	defer zipW.Close()

	gameName, err := getCurrentGameName()
	if err != nil {
		zap.S().Errorf("Failed to create backup: %v", err)
		return err
	}
	oldGameName := gameName + "_old"

	err = filepath.Walk(GameDataPath, func(path string, info fs.FileInfo, err error) error {
		if err != nil {
			return err
		}

		// skip ErrorLogs folder
		if info.IsDir() && info.Name() == "ErrorLogs" {
			return filepath.SkipDir
		}

		// skip "_old" files
		if info.Name() == oldGameName || info.Name() == OldSaveGameInfo {
			return nil
		}

		// skip dirs
		if info.IsDir() {
			return nil
		}

		header, err := zip.FileInfoHeader(info)
		if err != nil {
			return err
		}
		header.Method = zip.Deflate
		header.Name, err = filepath.Rel(GameDataPath, path)
		if err != nil {
			return err
		}

		headerWriter, err := zipW.CreateHeader(header)
		if err != nil {
			return err
		}

		f, err := os.Open(path)
		if err != nil {
			return err
		}
		defer f.Close()

		zap.S().Debugf("Adding To Archive: %v", header.Name)
		_, err = io.Copy(headerWriter, f)
		return err

	})

	if err != nil {
		zap.S().Errorf("Failed walking Stardew data folder: %v", err)
		return err
	}

	return nil
}

type GameLoaderJsonData struct {
	SaveNameToLoad string `json:"SaveNameToLoad"`
}

func getCurrentGameName() (string, error) {
	loaderJson, err := ioutil.ReadFile(GameLoaderJsonPath)
	if err != nil {
		zap.S().Errorf("Couldn't find GameLoader json file: %v", err)
		return "", err
	}

	var loaderData GameLoaderJsonData
	err = json.Unmarshal(loaderJson, &loaderData)

	if err != nil {
		zap.S().Errorf("Couldn't parse GameLoader json: %v", err)
		return "", err
	}

	return loaderData.SaveNameToLoad, nil
}
