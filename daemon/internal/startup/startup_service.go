package startup

import (
	"archive/zip"
	"bufio"
	"context"
	"encoding/json"
	"fmt"
	"io"
	"os"
	"sync"

	"cloud.google.com/go/storage"
	pbsm "github.com/junimohost/game-daemon/gen/proto/go/junimohost/servermanager/v1"
	pbsd "github.com/junimohost/game-daemon/gen/proto/go/junimohost/stardewdaemon/v1"
	"github.com/junimohost/game-daemon/internal/backup"
	"github.com/junimohost/game-daemon/internal/util"
	"github.com/junimohost/game-daemon/internal/zips"
	"github.com/junimohost/game-daemon/pkg/functional"
	"go.uber.org/zap"
)

const (
	bufferSize    = 5120
	ModFolder     = "/data/Stardew/Stardew Valley/Mods"
	ModsStateFile = ModFolder + "/state.json"
)

type NewGameConfig struct {
	WhichFarm          int    `json:"WhichFarm"`
	UseSeparateWallets bool   `json:"UseSeparateWallets"`
	StartingCabins     int    `json:"StartingCabins"`
	CatPerson          bool   `json:"CatPerson"`
	FarmName           string `json:"FarmName"`
	MaxPlayers         int    `json:"MaxPlayers"`
	CabinStrategy      int    `json:"CabinStrategy"`
}

type loadedModSavedPaths struct {
	VersionId string   `json:"VersionId,omitempty"`
	Paths     []string `json:"Paths,omitempty"`
}

type modVersionPathDestination struct {
	VersionId string
	Path      string
}

type Service struct {
	backendAvailable      bool
	startupScriptFinished bool
	config                pbsm.GameConfig

	storageClient       *storage.Client
	backupService       *backup.Service
	stardewDaemonClient pbsd.StardewDaemonServiceClient
	serverManagerClient pbsm.ServerManagerServiceClient
	serverId            string
}

func NewService(
	storageClient *storage.Client,
	backupService *backup.Service,
	stardewDaemonClient pbsd.StardewDaemonServiceClient,
	serverManagerClient pbsm.ServerManagerServiceClient,
	serverId string,
	backendAvailable bool) *Service {
	return &Service{
		backendAvailable:      backendAvailable,
		storageClient:         storageClient,
		backupService:         backupService,
		stardewDaemonClient:   stardewDaemonClient,
		serverManagerClient:   serverManagerClient,
		serverId:              serverId,
		startupScriptFinished: false,
		//default config for when backend is unavailable
		config: pbsm.GameConfig{
			WhichFarm:          0,
			UseSeparateWallets: false,
			StartingCabins:     1,
			CatPerson:          false,
			FarmName:           "Test",
			ModVersionIds:      nil,
			MaxPlayers:         10,
			CabinStrategy:      0,
		},
	}
}

func (s *Service) RunStartupScript(ctx context.Context) error {
	defer func() {
		zap.S().Info("Finished Startup Script")
		s.startupScriptFinished = true
	}()

	startupConfig, err := s.stardewDaemonClient.GetStartupConfig(ctx, &pbsd.GetStartupConfigRequest{
		ServerId: s.serverId,
	})

	if err != nil {
		zap.S().Errorf("Failed to fetch startup config: %v", err)
		return err
	}

	if startupConfig.BackupPath != "" {
		err = s.backupService.RestoreBackup(ctx, startupConfig.BackupPath)
		if err != nil {
			return err
		}

		_, err := s.stardewDaemonClient.UpdateStatus(ctx, &pbsd.UpdateStatusRequest{
			ServerId:                s.serverId,
			BackupRestoreSuccessful: true,
		})
		if err != nil {
			return err
		}
	}

	config := startupConfig.Config

	if config == nil {
		return nil
	}

	s.config = *config
	s.setupModsForServer(ctx, config.ModVersionIds)

	return nil
}

func (s *Service) setupModsForServer(ctx context.Context, modVersionIdsToDownload []string) {
	zap.S().Infof("Downloading mods: %v", modVersionIdsToDownload)

	response, err := s.serverManagerClient.GetAvailableMods(ctx, &pbsm.GetAvailableModsRequest{})
	if err != nil {
		return
	}

	allMods := response.Mods
	allVersions := functional.FlatMapSlice(allMods, func(m *pbsm.Mod) []*pbsm.ModVersion {
		if m != nil {
			return m.Versions
		}
		var temp []*pbsm.ModVersion
		return temp
	})

	idsToDownloadPaths := map[string]string{}

	for _, versionId := range modVersionIdsToDownload {
		matched := functional.Find(allVersions, func(m *pbsm.ModVersion) bool { return m.ModVersionId == versionId })
		if matched.GcsPath == "" {
			zap.S().Errorf("Could not find GCS Path for Version ID: %v", modVersionIdsToDownload)
			continue
		}
		idsToDownloadPaths[versionId] = matched.GcsPath
	}

	var wg sync.WaitGroup
	wg.Add(len(idsToDownloadPaths))

	outputFolderNameChan := make(chan modVersionPathDestination)
	errorChan := make(chan error)

	for versionId, gcsPath := range idsToDownloadPaths {
		go s.extractModZipFromGcsToLocal(ctx, versionId, gcsPath, outputFolderNameChan, errorChan, &wg)
	}

	wg.Wait()
	close(outputFolderNameChan)
	close(errorChan)

	writeStateFileFromOutputChannel(outputFolderNameChan)

	for err := range errorChan {
		zap.S().Errorf("Received error from mod goroutine: %v", err)
	}
}

func writeStateFileFromOutputChannel(outputFolderNameChan chan modVersionPathDestination) {
	var modStateMap map[string]*loadedModSavedPaths

	for output := range outputFolderNameChan {
		if val, ok := modStateMap[output.VersionId]; ok {
			val.Paths = append(val.Paths, output.Path)
		} else {
			modStateMap[output.VersionId] = &loadedModSavedPaths{
				VersionId: output.VersionId,
				Paths:     []string{output.Path},
			}
		}
	}

	var modPaths []loadedModSavedPaths
	for _, val := range modStateMap {
		modPaths = append(modPaths, *val)
	}

	marshal, err := json.Marshal(modPaths)
	if err != nil {
		return
	}

	_ = os.WriteFile(ModsStateFile, marshal, 0644)
}

func (s *Service) extractModZipFromGcsToLocal(
	ctx context.Context,
	versionId,
	gcsPath string,
	outputFolderNameChan chan modVersionPathDestination,
	errorChan chan error,
	wg *sync.WaitGroup,
) {
	defer wg.Done()
	bucketName, objectName := util.GetBucketAndObjectFromGcsPath(gcsPath)
	gcsR, err := s.storageClient.Bucket(bucketName).Object(objectName).NewReader(ctx)
	if err != nil {
		errorChan <- err
		return
	}
	defer gcsR.Close()
	// TODO: delete if folder exists
	fileName := fmt.Sprintf("%v/%v.zip", os.TempDir(), versionId)

	err = bufferedWriteFromGcsToFile(gcsR, fileName)
	if err != nil {
		errorChan <- err
		return
	}

	zipReader, err := zip.OpenReader(fileName)
	defer zipReader.Close()
	if err != nil {
		errorChan <- err
		return
	}

	outputDirs := zips.ExtractZipToPath(ModFolder, &zipReader.Reader)

	for _, dir := range outputDirs {
		outputFolderNameChan <- modVersionPathDestination{
			VersionId: versionId,
			Path:      dir,
		}
	}

	err = os.Remove(fileName)
	if err != nil {
		zap.S().Errorf("Failed to delete zip file %v", err)
	}
}

func bufferedWriteFromGcsToFile(gcsReader *storage.Reader, fileName string) error {
	buffer := make([]byte, bufferSize)
	f, err := os.Create(fileName)
	defer f.Close()
	w := bufio.NewWriter(f)
	_, err = io.CopyBuffer(w, gcsReader, buffer)
	if err != nil {
		return err
	}

	err = w.Flush()
	if err != nil {
		return err
	}
	return nil
}

func (s *Service) IsStartupScriptFinished() bool {
	if !s.backendAvailable {
		return true
	}
	return s.startupScriptFinished
}

func (s *Service) UpdateStatus(update StatusUpdate) error {
//	_, err := s.stardewDaemonClient.UpdateStatus(context.Background(), &pbsd.UpdateStatusRequest{
//		ServerId:                  s.serverId,
//		BackupRestoreSuccessfulV2: pbsd.Status(update.BackupRestoreSuccessful),
//		ServerConnectable:         pbsd.Status(update.ServerConnectable),
//	})

//	if err != nil {
//		return err
//	}

	return nil
}
