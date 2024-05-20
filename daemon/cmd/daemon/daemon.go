package main

import (
	"context"
	"os"
	"os/signal"
	"syscall"

	"cloud.google.com/go/storage"
	"github.com/gin-gonic/gin"
	pbsm "github.com/junimohost/game-daemon/gen/proto/go/junimohost/servermanager/v1"
	pbsd "github.com/junimohost/game-daemon/gen/proto/go/junimohost/stardewdaemon/v1"
	"github.com/junimohost/game-daemon/internal/backup"
	"github.com/junimohost/game-daemon/internal/startup"
	"github.com/junimohost/game-daemon/pkg/envutil"
	"github.com/junimohost/game-daemon/pkg/logging"
	"go.uber.org/zap"
	"google.golang.org/grpc"
	"google.golang.org/grpc/credentials/insecure"
)

func main() {
	logger := logging.CreateLogger()
	defer logger.Sync()

	undo := zap.ReplaceGlobals(logger.Desugar())
	defer undo()

	ctx := context.Background()
	ginServer := gin.Default()

	serverID := envutil.GetEnvOrFail("SERVER_ID")
	backupBucketName := envutil.GetEnvOrFail("BACKUP_GCS_BUCKET")

	noBackendEnv := envutil.GetEnvOrDefault("NO_BACKEND", "")
	backendAvailable := noBackendEnv != "true"

	var backendServerAddress string
	if backendAvailable {
		backendServerAddress = envutil.GetEnvOrFail("BACKEND_HOSTPORT")
	} else {
		backendServerAddress = ""
	}

	storageClient, err := storage.NewClient(ctx)
	if err != nil {
		logger.Errorf("Failed to start GCS client: %v", err)
	}

	var stardewDaemonServiceClient pbsd.StardewDaemonServiceClient
	var serverManagerServiceClient pbsm.ServerManagerServiceClient
	if backendAvailable {
		conn, err := grpc.Dial(backendServerAddress, grpc.WithTransportCredentials(insecure.NewCredentials()))
		if err != nil {
			logger.Fatalf("Failed to connect to backend: %v", err)
		}
		defer conn.Close()
		stardewDaemonServiceClient = pbsd.NewStardewDaemonServiceClient(conn)
		serverManagerServiceClient = pbsm.NewServerManagerServiceClient(conn)
	}

	backupService := backup.NewService(storageClient, stardewDaemonServiceClient, backupBucketName, serverID, backendAvailable)
	backupController := backup.NewController(backupService)

	backupController.AddRoutes(ginServer)

	startupService := startup.NewService(storageClient, backupService, stardewDaemonServiceClient, serverManagerServiceClient, serverID, backendAvailable)
	startupController := startup.NewController(startupService)

	startupController.AddRoutes(ginServer)

	httpPort := envutil.GetEnvOrDefault("DAEMON_HTTP_PORT", "8080")
	go func() {
		if err := ginServer.Run("0.0.0.0:" + httpPort); err != nil {
			logger.Fatalf("Failed to serve gin server: %v", err)
		}
	}()

	if backendAvailable {
		logger.Debugf("Running Startup Script!")
		err := startupService.RunStartupScript(ctx)
		if err != nil {
			logger.Errorf("Failed to complete startup script: %v", err)
		}
	}

	logger.Infof("game-daemon started on: %v", httpPort)

	c := make(chan os.Signal, 1)
	signal.Notify(c, os.Interrupt, syscall.SIGINT, syscall.SIGTERM)
	<-c
}
