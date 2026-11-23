param([string]$BaseUrl = 'http://localhost:5080')
$ErrorActionPreference = 'Stop'
$uri = "$($BaseUrl.TrimEnd('/'))/api/v1/reservations"
$samples = @(
    @{ hotelId = 'H_BEIJING'; startDate = '2026-11-06'; endDate = '2026-11-09'; roomNumber = 101; guestId = 'a2792706-3c1a-43da-a375-d4cfa471de5e' },
    @{ hotelId = 'H_BEIJING'; startDate = '2026-11-06'; endDate = '2026-11-08'; roomNumber = 101; guestId = '849f7147-abbe-42ac-9516-d494a3127475' },
    @{ hotelId = 'H_BEIJING'; startDate = '2026-11-07'; endDate = '2026-11-10'; roomNumber = 202; guestId = 'cc036733-0da4-40b3-937b-c5ef63778509' }
)
foreach ($sample in $samples) {
    $reservation = Invoke-RestMethod -Method Post -Uri $uri -ContentType 'application/json' -Body ($sample | ConvertTo-Json)
    $reservation | Select-Object confirmationNumber, hotelId, startDate, roomNumber
}
