package refutil

// From https://stackoverflow.com/a/55321839

import "time"

func Bool(i bool) *bool {
	return &i
}

func Int(i int) *int {
	return &i
}

func Int32(i int32) *int32 {
	return &i
}

func Int64(i int64) *int64 {
	return &i
}

func String(i string) *string {
	return &i
}

func Duration(i time.Duration) *time.Duration {
	return &i
}

func Strings(ss []string) []*string {
	r := make([]*string, len(ss))
	for i := range ss {
		r[i] = &ss[i]
	}
	return r
}
