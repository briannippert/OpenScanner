SELECT DISTINCT strftime('%d', timestamp) FROM transmissions WHERE strftime('%Y', timestamp) = @Year AND strftime('%m', timestamp) = @Month ORDER BY 1 DESC
