SELECT DISTINCT strftime('%m', timestamp) FROM transmissions WHERE strftime('%Y', timestamp) = @Year ORDER BY 1 DESC
