using Microsoft.EntityFrameworkCore;
using RinhaBackend.Database.Models;

namespace RinhaBackend.Database;

public class PaymentProcessorDbContext(DbContextOptions<PaymentProcessorDbContext> options) : DbContext(options)
{
    public DbSet<PaymentRequest> PaymentRequests => Set<PaymentRequest>();
}