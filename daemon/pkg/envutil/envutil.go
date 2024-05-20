package envutil

import (
	"go.uber.org/zap"
	"os"
	"strconv"
)

func GetInt64EnvOrFail(envKey string) int64 {
	value := GetEnvOrFail(envKey)
	intValue, err := strconv.Atoi(value)
	if err != nil {
		zap.S().Fatalf(envKey + " env variable not an int!")
	}
	return int64(intValue)
}

func GetIntEnvOrFail(envKey string) int {
	value := GetEnvOrFail(envKey)
	intValue, err := strconv.Atoi(value)
	if err != nil {
		zap.S().Fatalf(envKey + " env variable not an int!")
	}
	return intValue
}

func GetEnvOrFail(envKey string) string {

	value := os.Getenv(envKey)
	if value == "" {
		zap.S().Fatalf(envKey + " env variable not set!")
	}

	return value
}

func GetEnvOrDefault(envKey, defaultVal string) string {

	value := os.Getenv(envKey)
	if value == "" {
		zap.S().Infof("%v env variable not set defaulting to %v", envKey, defaultVal)
		return defaultVal
	}

	return value
}
