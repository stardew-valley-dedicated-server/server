package startup

import (
	"net/http"

	"github.com/gin-gonic/gin"
)

type StatusUpdate struct {
	BackupRestoreSuccessful int `json:"BackupRestoreSuccessful,omitempty"`
	ServerConnectable       int `json:"ServerConnectable,omitempty"`
}

type Controller struct {
	startupService *Service
}

func NewController(startupService *Service) *Controller {
	return &Controller{startupService: startupService}
}

func (c *Controller) AddRoutes(gin *gin.Engine) {
	gin.GET("/startup", c.OnGetStartup)
	gin.GET("/config", c.OnGetConfig)
	gin.POST("/status", c.OnUpdateStatus)
}

func (c *Controller) OnGetStartup(ctx *gin.Context) {
	if c.startupService.IsStartupScriptFinished() {
		ctx.Status(http.StatusOK)
	} else {
		ctx.Status(http.StatusAccepted)
	}
}

func (c *Controller) OnGetConfig(ctx *gin.Context) {
	ctx.JSON(200, NewGameConfig{
		WhichFarm:          int(c.startupService.config.WhichFarm),
		UseSeparateWallets: c.startupService.config.UseSeparateWallets,
		StartingCabins:     int(c.startupService.config.StartingCabins),
		CatPerson:          c.startupService.config.CatPerson,
		FarmName:           c.startupService.config.FarmName,
		MaxPlayers:         int(c.startupService.config.MaxPlayers),
		CabinStrategy:      int(c.startupService.config.CabinStrategy),
	})
}

func (c *Controller) OnUpdateStatus(ctx *gin.Context) {
	var payload StatusUpdate
	err := ctx.ShouldBindJSON(&payload)
	if err != nil {
		ctx.Status(http.StatusBadRequest)
		return
	}

	err = c.startupService.UpdateStatus(payload)

	if err != nil {
		ctx.Status(http.StatusBadRequest)
		return
	}

	ctx.Status(http.StatusOK)
}