SELECT DISTINCT alphaTag, frequency FROM transmissions WHERE strftime('%Y', timestamp) = @Year AND strftime('%m', timestamp) = @Month AND strftime('%d', timestamp) = @Day ORDER BY alphaTag, frequency
