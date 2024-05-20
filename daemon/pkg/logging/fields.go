package logging

import "go.uber.org/zap"

func ServerID(serverID string) zap.Field {
	return zap.String("serverID", serverID)
}

func SubscriptionID(subscriptionID string) zap.Field {
	return zap.String("subscriptionID", subscriptionID)
}

func WorkflowID(workflowID string) zap.Field {
	return zap.String("workflowID", workflowID)
}
