package stardewtypes

type Season int

const (
	SPRING Season = 0
	SUMMER Season = 1
	FALL   Season = 2
	WINTER Season = 3
)

type SDate struct {
	Day    int    `json:"Day"`
	Season Season `json:"Season"`
	Year   int    `json:"Year"`
}
