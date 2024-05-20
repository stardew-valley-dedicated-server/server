package functional

func MapSlice[T any, M any](target []T, f func(T) M) []M {
	n := make([]M, len(target))
	for i, val := range target {
		n[i] = f(val)
	}
	return n
}

func FlatMapSlice[T any, M any](target []T, f func(T) []M) []M {
	var n []M
	for _, val := range target {
		for _, item := range f(val) {
			n = append(n, item)
		}
	}
	return n
}

func Filter[T any](slice []T, f func(T) bool) []T {
	var n []T
	for _, e := range slice {
		if f(e) {
			n = append(n, e)
		}
	}
	return n
}

func Find[T any](slice []T, f func(T) bool) T {
	var result T
	for _, e := range slice {
		if f(e) {
			return e
		}
	}
	return result
}
