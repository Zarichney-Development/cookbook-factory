# Cookbook API AWS Maintenance Guide

## One-Time Workstation Setup
1. **Set up SSH Config**:
```powershell
# Create SSH directory if it doesn't exist
mkdir ~/.ssh -Force

# Add SSH config entry
@"
Host cookbook-api
    HostName <EC2_PUBLIC_DNS>
    User ec2-user
    IdentityFile ~/.ssh/github-cookbook-to-ec2.pem
"@ | Add-Content ~/.ssh/config

# Move key to SSH directory and set permissions
Copy-Item github-cookbook-to-ec2.pem ~/.ssh/
icacls ~/.ssh/github-cookbook-to-ec2.pem /inheritance:r /grant:r "$($env:USERNAME):(R)"
```

## Initialize Session Variables
```powershell
# Core variables
$appName = "cookbook-api"
$region = "us-east-2"
$sgId = "sg-00b18fae24f53e666"
$cfDistId = (aws cloudfront list-distributions --query "DistributionList.Items[?Aliases.Items[?contains(@, 'zarichney.com')]].Id" --output text)
$instanceId = (aws ec2 describe-instances --filters "Name=tag:Name,Values=${appName}" --query "Reservations[0].Instances[0].InstanceId" --output text)
$ec2Host = (aws ec2 describe-instances --filters "Name=tag:Name,Values=${appName}" --query "Reservations[0].Instances[0].PublicDnsName" --output text)

# Update SSH config with current EC2 host
(Get-Content ~/.ssh/config) | 
    ForEach-Object {$_ -replace 'HostName .*', "HostName ${ec2Host}"} | 
    Set-Content ~/.ssh/config

# Echo values for verification
Write-Host "App Name: ${appName}"
Write-Host "Region: ${region}"
Write-Host "Security Group: ${sgId}"
Write-Host "CloudFront Distribution: ${cfDistId}"
Write-Host "Instance ID: ${instanceId}"
Write-Host "EC2 Host: ${ec2Host}"
```

## Common Commands

### EC2 Access & Management
```powershell
# Get EC2 instance info
aws ec2 describe-instances --filters "Name=tag:Name,Values=${appName}" --query "Reservations[0].Instances[0]"

# SSH into instance
ssh cookbook-api

# View application logs (when SSH'd in)
ssh cookbook-api "sudo journalctl -u ${appName} -f"

# Restart service
ssh cookbook-api "sudo systemctl restart ${appName}"

# Check service status
ssh cookbook-api "sudo systemctl status ${appName}"
```

### Security Groups
```powershell
# List security group rules
aws ec2 describe-security-groups --group-ids ${sgId}

# Add new inbound rule
aws ec2 authorize-security-group-ingress `
    --group-id ${sgId} `
    --protocol tcp `
    --port 443 `
    --cidr 0.0.0.0/0

# Update SSH access
aws ec2 update-security-group-rule-descriptions-ingress `
    --group-id ${sgId} `
    --ip-permissions "IpProtocol=tcp,FromPort=22,ToPort=22,IpRanges=[{CidrIp=0.0.0.0/0}]"
```

### CloudFront
```powershell
# Create cache invalidation
aws cloudfront create-invalidation --distribution-id ${cfDistId} --paths "/api/cookbook/*"
```

### Secrets & Configuration
```powershell
# View Parameter Store secrets
aws ssm get-parameter --name "/${appName}/api-key" --with-decryption

# Update Parameter Store secret
aws ssm put-parameter `
    --name "/${appName}/api-key" `
    --value "new-value" `
    --type SecureString `
    --overwrite
```

### Data Management
```powershell
# Backup data locally
$timestamp = Get-Date -Format "yyyy-MM-dd-HHmm"
mkdir -Force "./backup-${timestamp}"
scp -r cookbook-api:/var/lib/${appName}/data/* "./backup-${timestamp}/"

# Restore data to EC2
scp -r "./backup-latest/*" cookbook-api:/var/lib/${appName}/data/
```

## Important Paths
- Application: `/opt/${appName}/`
- Data: `/var/lib/${appName}/data/`
- Logs: `/var/log/${appName}/`
- Service File: `/etc/systemd/system/${appName}.service`
- CloudWatch Logs: `/aws/${appName}`

## Health Checks
```powershell
# Public health check
curl "https://zarichney.com/api/cookbook/factory/health"

# Secure health check
$apiKey = (aws ssm get-parameter --name "/${appName}/api-key" --with-decryption --query "Parameter.Value" --output text)
curl -H "X-Api-Key: ${apiKey}" "https://zarichney.com/api/cookbook/factory/health/secure"
```

## Troubleshooting Tips
1. **Service Won't Start**:
   ```powershell
   # Check logs
   ssh cookbook-api "sudo journalctl -u ${appName} -n 50"
   
   # Verify permissions
   ssh cookbook-api "ls -la /opt/${appName}/"
   ssh cookbook-api "ls -la /var/lib/${appName}/data/"
   ```

2. **SSH Connection Issues**:
   - Verify SSH config is correct: `cat ~/.ssh/config`
   - Check key permissions: `icacls ~/.ssh/github-cookbook-to-ec2.pem`
   - Test connection: `ssh -v cookbook-api`

3. **API Unreachable**:
   - Test health endpoint directly on EC2
   ```powershell
   ssh cookbook-api "curl http://localhost:5000/api/factory/health"
   ```

## Regular Maintenance
1. Monitor CloudWatch logs for errors
   ```powershell
   aws logs get-log-events --log-group-name "/aws/${appName}" --log-stream-name ${instanceId}
   ```

2. Backup data folder
   ```powershell
   $timestamp = Get-Date -Format "yyyy-MM-dd-HHmm"
   mkdir -Force "./backup-${timestamp}"
   scp -r cookbook-api:/var/lib/${appName}/data/* "./backup-${timestamp}/"
   ```

3. Review security group rules
   ```powershell
   aws ec2 describe-security-groups --group-ids ${sgId} --output table
   ```

4. Check EC2 metrics
   ```powershell
   aws cloudwatch get-metric-statistics `
       --namespace AWS/EC2 `
       --metric-name CPUUtilization `
       --dimensions Name=InstanceId,Value=${instanceId} `
       --start-time (Get-Date).AddHours(-1).ToString("yyyy-MM-ddTHH:mm:ss") `
       --end-time (Get-Date).ToString("yyyy-MM-ddTHH:mm:ss") `
       --period 300 `
       --statistics Average
   ```

5. Rotate API key
   ```powershell
   $newApiKey = [System.Guid]::NewGuid().ToString()
   aws ssm put-parameter --name "/${appName}/api-key" --value ${newApiKey} --type SecureString --overwrite
   Write-Host "New API Key: ${newApiKey}"
   ```