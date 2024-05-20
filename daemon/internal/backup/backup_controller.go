package backup

import (
	"encoding/json"
	"github.com/gin-gonic/gin"
	"github.com/junimohost/game-daemon/pkg/stardewtypes"
	"go.uber.org/zap"
	"io/ioutil"
	"net/http"
)

type CreateBackupRequest struct {
	Date stardewtypes.SDate `json:"date"`
}

type Controller struct {
	backupService *Service
}

func NewController(backupService *Service) *Controller {
	return &Controller{backupService: backupService}
}

func (c *Controller) AddRoutes(gin *gin.Engine) {
	gin.POST("/backup", c.onCreateBackup)
}

func (c *Controller) onCreateBackup(ctx *gin.Context) {
	body, err := ioutil.ReadAll(ctx.Request.Body)

	if err != nil {
		zap.S().Errorf("Error reading request body: %v", err)
		ctx.Status(http.StatusBadRequest)
		return
	}

	var createBackupRequest CreateBackupRequest
	err = json.Unmarshal(body, &createBackupRequest)
	if err != nil {
		zap.S().Errorf("Error parsing request body: %v", err)
		ctx.Status(http.StatusBadRequest)
		return
	}

	err = c.backupService.CreateBackup(ctx, createBackupRequest.Date)
	if err != nil {
		zap.S().Errorf("Error creating backup: %v", err)
		ctx.Status(http.StatusInternalServerError)
		return
	}

	zap.S().Infof("Created Backup")
	ctx.Status(http.StatusOK)
}
